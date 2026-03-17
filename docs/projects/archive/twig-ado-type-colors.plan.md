---
goal: Fetch ADO work item type color and icon data and persist to local storage
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-15
owner: Twig CLI team
tags: [feature, ado, data-pipeline, infrastructure]
revision_notes: "Rev 1.1: Address technical review feedback — clarify SQLite persistence timing (refresh-only, not init), specify SqliteCacheStore injection in RefreshCommand, resolve test file naming contradictions, fix stale claim about TwigConfigurationTests."
---

# ADO Type Color Fetching — Solution Design & Implementation Plan

## Executive Summary

Azure DevOps assigns each work item type a hex color and icon (e.g., Bug = `CC293D` / `icon_insect`). The Twig CLI already calls the `_apis/wit/workitemtypes` endpoint during `twig init` to detect the process template, but discards the `color` and `icon` fields from the response. This plan extends the existing data pipeline to capture those fields in the `AdoWorkItemTypeResponse` DTO, surface them through a new `IIterationService.GetWorkItemTypeAppearancesAsync` method, and store them in the `TwigConfiguration` JSON config file during both `twig init` and `twig refresh`. During `twig refresh`, type appearances are additionally persisted to the SQLite `process_types` table (new `color_hex` and `icon_id` columns) for queryability. The SQLite write is refresh-only because `twig init` creates an empty schema and does not populate `process_types` rows. The design strictly covers the data pipeline — no formatter or rendering changes are included.

---

## Background

### Current system state

- **`AdoIterationService.DetectProcessTemplateAsync`** (`src/Twig.Infrastructure/Ado/AdoIterationService.cs:64–93`) calls `GET {org}/{project}/_apis/wit/workitemtypes?api-version=7.1` and deserializes the response into `AdoWorkItemTypeListResponse`. It iterates `result.Value` but only reads `type.Name` for process template heuristics. The `color` and `icon` fields present in the ADO JSON are silently dropped because `AdoWorkItemTypeResponse` does not declare them.

- **`AdoWorkItemTypeResponse`** (`src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs`) has three properties: `Name`, `Description`, `ReferenceName`. The ADO API also returns `color` (6-digit hex string without `#`), `icon` (object with `id` string and `url` string), and `isDisabled` (boolean).

- **`TwigConfiguration`** (`src/Twig.Infrastructure/Config/TwigConfiguration.cs`) persists to `.twig/config` as JSON. It has `SeedConfig.DefaultChildType` (a `Dictionary<string, string>?`) but no property for type appearance data.

- **SQLite schema** (`SqliteCacheStore.Ddl`) has a `process_types` table with columns `type_name`, `states_json`, `default_child_type`, `valid_child_types_json`, `last_synced_at`. No columns for color or icon.

- **Domain layer** (`Twig.Domain`) has `WorkItemType` (value object, `readonly record struct`) with `Value` (name string) and static well-known instances. It has zero infrastructure dependencies — by DDD convention it must not reference DTOs, HTTP, or persistence.

- **`IIterationService`** (`src/Twig.Domain/Interfaces/IIterationService.cs`) exposes `GetCurrentIterationAsync` and `DetectProcessTemplateAsync`. No method returns type appearance data.

### Prior art

The `twig-color-wiring.plan.md` (rev 2) established a hardcoded `GetTypeColor` switch in `HumanOutputFormatter` that maps type names to ANSI color constants. That plan noted ADO's native type colors but chose hardcoded values for the initial implementation. This plan provides the data pipeline to eventually replace those hardcoded colors with the project's actual ADO type colors.

---

## Problem Statement

1. **Type color data is discarded**: The `DetectProcessTemplateAsync` method already receives a complete work item type listing from ADO (including `color` and `icon`), but the DTO drops those fields. A second API call would be wasteful.
2. **No local store for type colors**: Neither `TwigConfiguration` nor the SQLite `process_types` table has columns/fields for color or icon. Even if the DTO captured the data, there is nowhere to persist it.
3. **No interface to surface type colors**: The domain `IIterationService` interface has no method that returns appearance data. Commands and formatters cannot access type colors without such a contract.

---

## Goals and Non-Goals

### Goals

- **G-1**: Extend `AdoWorkItemTypeResponse` to deserialize `color` (hex string) and `icon` (object with `id` and `url`) from the ADO REST response.
- **G-2**: Add a method to `IIterationService` that returns a dictionary of work item type name → appearance (color hex + icon ID).
- **G-3**: Create a domain-layer DTO (`WorkItemTypeAppearance` record) to carry color+icon data across the domain boundary without coupling to Infrastructure.
- **G-4**: Persist fetched type appearances in `TwigConfiguration` (`.twig/config` JSON) so they survive across CLI invocations.
- **G-5**: Persist fetched type appearances in the SQLite `process_types` table (new `color_hex` and `icon_id` columns) during `twig refresh` for queryability. Note: `twig init` creates the schema with the new columns but does not populate `process_types` rows — the table is first populated by `twig refresh`.
- **G-6**: Populate type appearances during `twig init` and `twig refresh`.

### Non-Goals

- **NG-1**: Formatter or rendering changes — no `HumanOutputFormatter`, ANSI, or UI changes.
- **NG-2**: Downloading icon SVG/PNG assets — only the icon ID string is stored.
- **NG-3**: Supporting custom work item types beyond what the ADO API returns — we store whatever ADO provides.
- **NG-4**: Version or changelog updates.
- **NG-5**: Changes to the `WorkItemType` value object — it remains a string-based struct with known-type validation.

---

## Requirements

### Functional Requirements

- **FR-001**: `AdoWorkItemTypeResponse` MUST include a `Color` property (`string?`, JSON name `"color"`) that captures the 6-digit hex color string (e.g., `"CC293D"`).
- **FR-002**: `AdoWorkItemTypeResponse` MUST include an `Icon` property (`AdoWorkItemTypeIconResponse?`, JSON name `"icon"`) where `AdoWorkItemTypeIconResponse` has `Id` (`string?`, JSON name `"id"`) and `Url` (`string?`, JSON name `"url"`).
- **FR-003**: A new domain record `WorkItemTypeAppearance` MUST be defined in `Twig.Domain/ValueObjects/` with properties `Name` (string), `Color` (string? — nullable hex without `#`), and `IconId` (string?).
- **FR-004**: `IIterationService` MUST expose a new method `Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default)`.
- **FR-005**: `AdoIterationService` MUST implement `GetWorkItemTypeAppearancesAsync` by calling the same `_apis/wit/workitemtypes` endpoint used by `DetectProcessTemplateAsync`, mapping each `AdoWorkItemTypeResponse` with non-null `Name` and `Color` to a `WorkItemTypeAppearance`.
- **FR-006**: `TwigConfiguration` MUST include a `TypeAppearances` property (`List<TypeAppearanceConfig>?`) where `TypeAppearanceConfig` is a POCO with `Name` (string), `Color` (string), and `IconId` (string?).
- **FR-007**: The `TwigJsonContext` source-gen context MUST include `[JsonSerializable]` attributes for `TypeAppearanceConfig`, `List<TypeAppearanceConfig>`, and `AdoWorkItemTypeIconResponse`.
- **FR-008**: The SQLite `process_types` table MUST be extended with `color_hex TEXT` and `icon_id TEXT` columns. This MUST be done by incrementing `SqliteCacheStore.SchemaVersion` to `2`, which triggers a full schema rebuild on next startup.
- **FR-009**: `InitCommand.ExecuteAsync` MUST call `GetWorkItemTypeAppearancesAsync`, populate `config.TypeAppearances`, and save the config. `InitCommand` does NOT write to the SQLite `process_types` table — the schema is created empty by `SqliteCacheStore` constructor, and rows are first populated by `twig refresh`.
- **FR-010**: `RefreshCommand.ExecuteAsync` MUST call `GetWorkItemTypeAppearancesAsync`, update the in-memory config's `TypeAppearances`, save the config, and update the `process_types` SQLite table via `SqliteCacheStore.GetConnection()`. `RefreshCommand` MUST receive `SqliteCacheStore` via constructor injection (it is already registered as a singleton in `Program.cs`). The SQLite write MUST use `INSERT OR REPLACE INTO process_types (type_name, ..., color_hex, icon_id, last_synced_at)` statements to upsert rows.
- **FR-011**: Disabled work item types (`isDisabled: true`) SHOULD be excluded from the stored appearances.

### Non-Functional Requirements

- **NFR-001**: All changes MUST be AOT-compatible. No reflection-based serialization. All new DTOs MUST be registered in `TwigJsonContext`.
- **NFR-002**: The `Twig.Domain` project MUST NOT gain any new package or project references. `WorkItemTypeAppearance` MUST have zero dependencies.
- **NFR-003**: The new `GetWorkItemTypeAppearancesAsync` method SHOULD reuse the same HTTP call pattern as existing methods in `AdoIterationService` (authenticated `SendAsync` → stream deserialization via source-gen JSON context).
- **NFR-004**: Schema version bump (1 → 2) MUST cause `SqliteCacheStore` to drop and recreate all tables. This is the existing behaviour and is acceptable for a local cache.

---

## Proposed Design

### Architecture Overview

```
ADO REST API                                                     
  GET {org}/{project}/_apis/wit/workitemtypes                    
       │                                                         
       ▼                                                         
  AdoWorkItemTypeListResponse                                    
    └─ List<AdoWorkItemTypeResponse>                             
         ├─ Name                                                 
         ├─ ReferenceName                                        
         ├─ Description                                          
         ├─ Color           ← NEW                                
         ├─ Icon            ← NEW (AdoWorkItemTypeIconResponse)  
         │    ├─ Id                                              
         │    └─ Url                                             
         └─ IsDisabled      ← NEW                                
       │                                                         
       │  AdoIterationService                                    
       │  .GetWorkItemTypeAppearancesAsync()                     
       ▼                                                         
  List<WorkItemTypeAppearance>    (Domain value object)          
       │                                                         
       ├──────────────────────────────┐                          
       ▼                              ▼                          
  TwigConfiguration               SQLite process_types           
  .TypeAppearances                 color_hex, icon_id columns    
  (persisted to .twig/config)      (persisted to .twig/twig.db)  
  ← written by init + refresh     ← written by refresh ONLY     
```

### Key Components

#### 1. `AdoWorkItemTypeResponse` (extended DTO)

**File**: `src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs`

```csharp
internal sealed class AdoWorkItemTypeResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("icon")]
    public AdoWorkItemTypeIconResponse? Icon { get; set; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; set; }
}

internal sealed class AdoWorkItemTypeIconResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
```

#### 2. `WorkItemTypeAppearance` (domain value object)

**File**: `src/Twig.Domain/ValueObjects/WorkItemTypeAppearance.cs`

```csharp
namespace Twig.Domain.ValueObjects;

/// <summary>
/// Visual appearance metadata for a work item type, fetched from Azure DevOps.
/// Immutable value object with no infrastructure dependencies.
/// </summary>
public sealed record WorkItemTypeAppearance(
    string Name,
    string Color,
    string? IconId);
```

This is a sealed record (not a `readonly record struct`) because it carries nullable reference-type data and is used as a list element — reference semantics are appropriate.

#### 3. `IIterationService` (extended interface)

**File**: `src/Twig.Domain/Interfaces/IIterationService.cs`

```csharp
public interface IIterationService
{
    Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default);
    Task<ProcessTemplate> DetectProcessTemplateAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default);
}
```

#### 4. `AdoIterationService` (extended implementation)

**File**: `src/Twig.Infrastructure/Ado/AdoIterationService.cs`

New method `GetWorkItemTypeAppearancesAsync`:

```csharp
public async Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default)
{
    var url = $"{_orgUrl}/{_project}/_apis/wit/workitemtypes?api-version={ApiVersion}";
    using var response = await SendAsync(url, ct);

    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWorkItemTypeListResponse, ct);

    if (result?.Value is null || result.Value.Count == 0)
        return Array.Empty<WorkItemTypeAppearance>();

    var appearances = new List<WorkItemTypeAppearance>();
    foreach (var type in result.Value)
    {
        if (type.Name is null || type.Color is null || type.IsDisabled)
            continue;

        appearances.Add(new WorkItemTypeAppearance(
            type.Name,
            type.Color,
            type.Icon?.Id));
    }

    return appearances;
}
```

#### 5. `TwigConfiguration` (extended config)

**File**: `src/Twig.Infrastructure/Config/TwigConfiguration.cs`

```csharp
public sealed class TwigConfiguration
{
    // ... existing properties ...
    public List<TypeAppearanceConfig>? TypeAppearances { get; set; }
}

public sealed class TypeAppearanceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string? IconId { get; set; }
}
```

#### 6. SQLite schema update

**File**: `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`

- Bump `SchemaVersion` from `1` to `2`.
- Add `color_hex TEXT` and `icon_id TEXT` columns to the `process_types` DDL.
- Add `type_colors` table drop to `DropAllTables` (if introduced) — but since the existing pattern drops and recreates all tables on version mismatch, the schema bump handles this automatically.

Updated DDL for `process_types`:

```sql
CREATE TABLE process_types (
    type_name TEXT PRIMARY KEY,
    states_json TEXT NOT NULL,
    default_child_type TEXT,
    valid_child_types_json TEXT,
    color_hex TEXT,
    icon_id TEXT,
    last_synced_at TEXT NOT NULL
);
```

### Data Flow

#### `twig init` flow (updated)

```
twig init --org myorg --project myproj
  │
  ├─ Create .twig/ directory
  ├─ Save initial TwigConfiguration (org, project)
  │
  ├─ iterationService.DetectProcessTemplateAsync()
  │    └─ GET _apis/wit/workitemtypes → template heuristic
  │
  ├─ iterationService.GetWorkItemTypeAppearancesAsync()     ← NEW
  │    └─ GET _apis/wit/workitemtypes → List<WorkItemTypeAppearance>
  │
  ├─ config.TypeAppearances = Map appearances to TypeAppearanceConfig list
  ├─ config.SaveAsync()  → .twig/config (JSON includes typeAppearances)
  │
  ├─ iterationService.GetCurrentIterationAsync()
  │    └─ GET _apis/work/teamsettings/iterations
  │
  └─ SqliteCacheStore constructor → creates schema v2 (process_types is EMPTY)
```

**Note on duplicate API calls**: `DetectProcessTemplateAsync` and `GetWorkItemTypeAppearancesAsync` both call the same endpoint. This is acceptable because: (a) `twig init` runs once and is not performance-sensitive, (b) the responses may be cached by the HTTP stack, and (c) merging the two methods would complicate the interface contract. If optimization is desired later, the two can be combined into a single internal call with caching.

**Note on init vs. refresh SQLite persistence**: After `twig init`, type appearances exist only in the config JSON file. The SQLite `process_types` table has the new `color_hex` and `icon_id` columns but zero rows. This is intentional — `InitCommand` constructs `SqliteCacheStore` locally and immediately disposes it (lines 76–77 of `InitCommand.cs`); it does not inject the store or use it beyond schema creation. Rows are first populated when the user runs `twig refresh`, where `RefreshCommand` receives `SqliteCacheStore` via constructor injection.

#### `twig refresh` flow (updated)

```
twig refresh
  │
  ├─ iterationService.GetCurrentIterationAsync()
  ├─ adoService.QueryByWiqlAsync(wiql) → fetch & cache work items
  │
  ├─ iterationService.GetWorkItemTypeAppearancesAsync()     ← NEW
  │    └─ List<WorkItemTypeAppearance>
  │
  ├─ Update config.TypeAppearances, save .twig/config
  │
  └─ For each appearance:                                    ← NEW
       INSERT OR REPLACE INTO process_types
         (type_name, states_json, default_child_type,
          valid_child_types_json, color_hex, icon_id, last_synced_at)
       via SqliteCacheStore.GetConnection()
```

**SQLite write mechanism**: `RefreshCommand` receives `SqliteCacheStore` via constructor injection (already registered as a singleton in `Program.cs`). It calls `SqliteCacheStore.GetConnection()` to obtain the `SqliteConnection`, then executes parameterized `INSERT OR REPLACE` statements. The `states_json` and other existing columns use empty/null defaults for rows that only carry color data — full process type sync is a separate concern.

### API Contracts

#### ADO REST API response (relevant fields)

```json
{
  "count": 15,
  "value": [
    {
      "name": "Bug",
      "referenceName": "contoso.VSTS.WorkItemTypes.Bug",
      "description": "...",
      "color": "CC293D",
      "icon": {
        "id": "icon_insect",
        "url": "https://dev.azure.com/fabrikam/_apis/wit/workItemIcons/icon_insect?color=CC293D&v=2"
      },
      "isDisabled": false
    }
  ]
}
```

#### `WorkItemTypeAppearance` (domain contract)

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Display name (e.g., `"Bug"`, `"User Story"`) |
| `Color` | `string?` | 6-digit hex color without `#` prefix (e.g., `"CC293D"`); nullable — ADO may omit for disabled/custom types |
| `IconId` | `string?` | Icon identifier (e.g., `"icon_insect"`), nullable |

#### `TypeAppearanceConfig` (config JSON contract)

```json
{
  "typeAppearances": [
    { "name": "Bug", "color": "CC293D", "iconId": "icon_insect" },
    { "name": "Epic", "color": "FF7B00", "iconId": "icon_crown" }
  ]
}
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Separate `GetWorkItemTypeAppearancesAsync` method** (not merged with `DetectProcessTemplateAsync`) | Single Responsibility — template detection returns `ProcessTemplate` enum; appearance fetching returns a list of appearance objects. Combining would require a composite return type. The duplicate HTTP call is acceptable for `init`/`refresh` (infrequent operations). |
| **`WorkItemTypeAppearance` in Domain layer** (not Infrastructure) | The interface `IIterationService` lives in Domain. Its methods must return domain types. A sealed record with zero deps satisfies DDD constraints. |
| **`sealed record` (not `readonly record struct`)** for `WorkItemTypeAppearance` | Contains nullable `IconId`, will be stored in lists — reference semantics are appropriate. Avoids boxing and default-value confusion with structs. |
| **Schema version bump (1→2) triggers full rebuild** | Existing `SqliteCacheStore` behaviour. The `process_types` table is a cache — rebuilding is safe. Adding columns to an existing schema via `ALTER TABLE` would require migration logic that doesn't exist yet. |
| **Store appearances in both config JSON and SQLite** | Config JSON is the primary persistence (survives `twig.db` deletion, human-readable). SQLite enables efficient lookups by type name for future rendering code. |
| **Filter out `isDisabled` types** | Disabled types cannot be used for work items. Including them would add noise. |
| **Store raw hex string without `#` prefix** | ADO returns hex without `#`. Storing as-is avoids transformation and makes round-tripping trivial. Formatters can prepend `#` when rendering. |

---

## Alternatives Considered

### Alt-1: Merge appearance data into `DetectProcessTemplateAsync` return type

Create a composite return type `ProcessDetectionResult { ProcessTemplate Template; List<WorkItemTypeAppearance> Appearances; }`.

**Pros**: Single API call; no duplicate HTTP request.
**Cons**: Changes the return type of an existing interface method — breaks existing consumers and tests. The composite type couples template detection to appearance fetching.
**Decision**: Rejected. Adding a separate method is additive and non-breaking.

### Alt-2: Store colors only in SQLite (not in config JSON)

**Pros**: Single storage location; SQLite is already the cache store.
**Cons**: SQLite `twig.db` is a disposable cache — users may delete it. Colors would be lost until next refresh. Config JSON is the durable store.
**Decision**: Rejected. Dual storage provides durability (config) + queryability (SQLite).

### Alt-3: Use a `Dictionary<string, string>` in TwigConfiguration for type→color mapping

**Pros**: Simpler config structure.
**Cons**: Loses icon information. Adding icon later would require a breaking config change.
**Decision**: Rejected. A typed list of `TypeAppearanceConfig` objects supports both color and icon from the start.

### Alt-4: Create a new `ITypeAppearanceService` interface

**Pros**: Clean separation of concerns.
**Cons**: Introduces a new interface, a new DI registration, and a new infrastructure class — all for a single method that calls the same endpoint as `IIterationService`. Over-engineering for the scope.
**Decision**: Rejected. Extending `IIterationService` is pragmatic and consistent with its role as the ADO metadata service.

---

## Dependencies

### Internal dependencies

| Dependency | Note |
|------------|------|
| `AdoWorkItemTypeResponse` DTO | Extended with 3 new properties |
| `AdoIterationService` | Extended with 1 new method |
| `IIterationService` interface | Extended with 1 new method (breaking for mock implementations in tests) |
| `TwigConfiguration` | Extended with `TypeAppearances` property |
| `TwigJsonContext` | New `[JsonSerializable]` attributes |
| `SqliteCacheStore` | Schema version bump + DDL update |
| `InitCommand` | New call to `GetWorkItemTypeAppearancesAsync` |
| `RefreshCommand` | New call to `GetWorkItemTypeAppearancesAsync` + config/SQLite persistence; new `SqliteCacheStore`, `TwigConfiguration`, and `TwigPaths` constructor dependencies |

### External dependencies

| Dependency | Note |
|------------|------|
| ADO REST API `_apis/wit/workitemtypes` (v7.1) | Already called; no new endpoint |

### Sequencing constraints

- Epic 1 (DTO + Domain model) MUST complete before Epic 2 (service + persistence).
- Epic 2 MUST complete before Epic 3 (command integration).
- Test updates within each epic SHOULD be done alongside the implementation.

---

## Impact Analysis

### Components affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `AdoWorkItemTypeResponse` | Additive (3 new properties) | Low — JSON deserialization ignores unknown props |
| `AdoWorkItemTypeIconResponse` | New class | Low — additive |
| `WorkItemTypeAppearance` | New domain record | Low — no existing code affected |
| `IIterationService` | Interface extension (1 new method) | **Medium** — breaks all mock implementations in tests |
| `AdoIterationService` | New method | Low — additive |
| `TwigConfiguration` | New property | Low — JSON deserialization ignores missing props |
| `TypeAppearanceConfig` | New class | Low — additive |
| `TwigJsonContext` | New attributes | Low — additive |
| `SqliteCacheStore` | Schema version bump (1→2) | **Medium** — existing DBs will be rebuilt |
| `InitCommand` | New logic added | Low — additive |
| `RefreshCommand` | Constructor change (3 new params) + new logic | **Medium** — constructor breaking change requires test updates |
| Test files (3+) | Mock updates for new interface method | Medium — systematic |

### Backward compatibility

- **Config file**: Adding `typeAppearances` to config JSON is backward-compatible. `LoadAsync` returns `null` for missing properties; the property is `List<TypeAppearanceConfig>?` (nullable).
- **SQLite DB**: Schema version 1→2 triggers a full rebuild. All cached work items and process types are dropped and recreated. This is by design — the SQLite DB is a disposable cache repopulated by `twig refresh`.
- **Interface**: Adding a method to `IIterationService` is a breaking change for any existing mock implementations. All test mocks must be updated to stub the new method.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Duplicate API call (DetectProcessTemplate + GetWorkItemTypeAppearances) | Certain | Low | Both are called only during `init`/`refresh` (infrequent). HTTP caching may deduplicate at transport level. Future optimization: internal caching of the API response. |
| Schema rebuild drops cached work items | Certain (on upgrade) | Low | `twig refresh` repopulates the cache. Users run `refresh` after `init`. |
| `IIterationService` interface change breaks test mocks | Certain | Low | Systematic mock update — all test files that mock `IIterationService` need one new stub line. |
| ADO API response shape changes | Low | Medium | The DTO uses `string?` for all new fields; null values are handled gracefully. `isDisabled` defaults to `false`. |

---

## Open Questions

1. **Should `DetectProcessTemplateAsync` and `GetWorkItemTypeAppearancesAsync` share a cached response?** — An internal `FetchWorkItemTypesAsync` method could cache the parsed response and serve both public methods. This would eliminate the duplicate HTTP call at the cost of adding cache-lifetime management. Recommendation: defer to a follow-up optimization if profiling shows the duplicate call is a problem.

2. ~~**Should `RefreshCommand` persist type appearances to SQLite `process_types`?**~~ — **RESOLVED**: Yes. SQLite writes happen during `twig refresh` only (not `twig init`). `RefreshCommand` receives `SqliteCacheStore` via constructor injection and uses `INSERT OR REPLACE` statements via `GetConnection()`. This avoids a second schema migration later and provides queryability for future rendering code. `twig init` persists to config JSON only; SQLite `process_types` rows are first created by `twig refresh`.

3. **Should `TypeAppearanceConfig` include `ReferenceName`?** — The ADO API returns both `name` and `referenceName`. Storing `referenceName` would enable matching by reference name (e.g., `contoso.VSTS.WorkItemTypes.Bug`). Recommendation: include it as an optional field for future-proofing.

---

## Implementation Phases

### Phase 1: DTO & Domain Model

**Exit criteria**: `AdoWorkItemTypeResponse` deserializes `color`, `icon`, and `isDisabled` from ADO JSON. `WorkItemTypeAppearance` record exists in Domain. All existing tests pass.

### Phase 2: Service & Persistence Layer

**Exit criteria**: `IIterationService.GetWorkItemTypeAppearancesAsync` is implemented. `TwigConfiguration.TypeAppearances` is a serializable property. SQLite schema is v2 with `color_hex`/`icon_id` columns. All existing tests pass (mocks updated).

### Phase 3: Command Integration

**Exit criteria**: `twig init` and `twig refresh` populate type appearances in config JSON. Integration tests verify the data pipeline end-to-end.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/WorkItemTypeAppearance.cs` | Domain value object carrying type name, hex color, and icon ID |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs` | Add `Color`, `Icon`, `IsDisabled` properties; add `AdoWorkItemTypeIconResponse` class |
| `src/Twig.Domain/Interfaces/IIterationService.cs` | Add `GetWorkItemTypeAppearancesAsync` method |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Implement `GetWorkItemTypeAppearancesAsync` |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `TypeAppearances` property and `TypeAppearanceConfig` class |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` attributes for new types |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Bump `SchemaVersion` to 2; add `color_hex`, `icon_id` columns to `process_types` DDL |
| `src/Twig/Commands/InitCommand.cs` | Call `GetWorkItemTypeAppearancesAsync`, populate config |
| `src/Twig/Commands/RefreshCommand.cs` | Call `GetWorkItemTypeAppearancesAsync`, update config + SQLite; add `TwigConfiguration`, `TwigPaths`, `SqliteCacheStore` constructor parameters |
| `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Update `FakeHandler.SetWorkItemTypesResponse` to include `color`/`icon`; add appearance tests |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | Update schema version assertions; verify new columns |
| `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Test serialization round-trip of `TypeAppearances` |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Update `IIterationService` mock to stub new method |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Update `IIterationService` mock; add test for appearance persistence |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: DTO & Domain Model

**Goal**: Extend the infrastructure DTO to capture ADO color/icon data and define a domain value object for type appearance.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add `Color` (`string?`, `[JsonPropertyName("color")]`) and `IsDisabled` (`bool`, `[JsonPropertyName("isDisabled")]`) properties to `AdoWorkItemTypeResponse`. Add `Icon` property (`AdoWorkItemTypeIconResponse?`, `[JsonPropertyName("icon")]`). Create `AdoWorkItemTypeIconResponse` class in the same file with `Id` (`string?`) and `Url` (`string?`) properties, both with `[JsonPropertyName]` attributes. | `src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs` | DONE |
| ITEM-002 | IMPL | Add `[JsonSerializable(typeof(AdoWorkItemTypeIconResponse))]` attribute to `TwigJsonContext`. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-003 | IMPL | Create `WorkItemTypeAppearance` as a `public sealed record` in `Twig.Domain/ValueObjects/` with constructor parameters `string Name`, `string? Color`, `string? IconId`. Add XML doc comment documenting sealed-record rationale and Color nullability contract. File must have `namespace Twig.Domain.ValueObjects;` and zero `using` statements (no dependencies). | `src/Twig.Domain/ValueObjects/WorkItemTypeAppearance.cs` | DONE |
| ITEM-004 | TEST | Update `FakeHandler.SetWorkItemTypesResponse` in `AdoIterationServiceTests.cs` to include `"color":"AABBCC"` and `"icon":{"id":"icon_test","url":"https://example.com"}` and `"isDisabled":false` in the generated JSON for each type. Verify all existing `DetectProcessTemplateAsync` tests still pass with the enriched JSON. Add `AdoWorkItemTypeResponse_Deserializes_ColorIconAndIsDisabled` test in `SqliteAndSerializationTests.cs` to assert Color, Icon.Id, Icon.Url, IsDisabled deserialization. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs`, `tests/Twig.Infrastructure.Tests/SqliteAndSerializationTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `AdoWorkItemTypeResponse` has `Color`, `Icon`, and `IsDisabled` properties
- [x] `AdoWorkItemTypeIconResponse` exists with `Id` and `Url` properties
- [x] `WorkItemTypeAppearance` record compiles with zero domain dependencies
- [x] All existing `AdoIterationServiceTests` pass with enriched JSON payloads
- [x] `TwigJsonContext` includes serializable attribute for `AdoWorkItemTypeIconResponse`

---

### EPIC-002: Service & Persistence Layer

**Goal**: Implement the `GetWorkItemTypeAppearancesAsync` method, extend config and SQLite storage.

**Prerequisites**: EPIC-001 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | Add `Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default)` to `IIterationService`. Add `using Twig.Domain.ValueObjects;` if not already present (it is — `IterationPath` is in that namespace). | `src/Twig.Domain/Interfaces/IIterationService.cs` | Done |
| ITEM-006 | IMPL | Implement `GetWorkItemTypeAppearancesAsync` in `AdoIterationService`. Call `GET {_orgUrl}/{_project}/_apis/wit/workitemtypes?api-version={ApiVersion}` via `SendAsync`. Deserialize with `TwigJsonContext.Default.AdoWorkItemTypeListResponse`. Filter out entries where `Name` is null, `Color` is null, or `IsDisabled` is true. Map to `List<WorkItemTypeAppearance>`. Return `Array.Empty<WorkItemTypeAppearance>()` if the response is empty/null. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Done |
| ITEM-007 | IMPL | Add `TypeAppearanceConfig` class to `TwigConfiguration.cs` with properties: `Name` (`string`, default `string.Empty`), `Color` (`string`, default `string.Empty`), `IconId` (`string?`). Add `public List<TypeAppearanceConfig>? TypeAppearances { get; set; }` property to `TwigConfiguration`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Done |
| ITEM-008 | IMPL | Add `[JsonSerializable(typeof(TypeAppearanceConfig))]` and `[JsonSerializable(typeof(List<TypeAppearanceConfig>))]` attributes to `TwigJsonContext`. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Done |
| ITEM-009 | IMPL | In `SqliteCacheStore`, change `SchemaVersion` from `1` to `2`. Update the `process_types` CREATE TABLE DDL to add `color_hex TEXT` and `icon_id TEXT` columns after `valid_child_types_json`. | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Done |
| ITEM-010 | TEST | Add tests to the existing `AdoIterationServiceTests.cs` file. Tests: (1) `GetWorkItemTypeAppearancesAsync_ReturnsMappedAppearances` — verify names, colors, iconIds are mapped correctly; (2) `GetWorkItemTypeAppearancesAsync_ExcludesDisabledTypes` — include a disabled type in the response, verify it is excluded; (3) `GetWorkItemTypeAppearancesAsync_EmptyResponse_ReturnsEmptyList`; (4) `GetWorkItemTypeAppearancesAsync_NullColor_ExcludesEntry`. Update `FakeHandler.SetWorkItemTypesResponse` to accept optional `color`, `icon`, and `isDisabled` parameters for these tests. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Done |
| ITEM-011 | TEST | Add config serialization round-trip test to the existing `TwigConfigurationTests.cs`: create a `TwigConfiguration` with `TypeAppearances` populated, serialize via `TwigJsonContext.Default.TwigConfiguration`, deserialize, verify `TypeAppearances` values match. | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Done |
| ITEM-012 | TEST | Update `SqliteCacheStoreTests` to verify schema version 2 and that `process_types` table has `color_hex` and `icon_id` columns. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | Done |

**Acceptance Criteria**:
- [x] `IIterationService` has `GetWorkItemTypeAppearancesAsync` method
- [x] `AdoIterationService` implementation correctly maps and filters type appearances
- [x] `TwigConfiguration` serializes/deserializes `TypeAppearances` via source-gen JSON
- [x] SQLite schema version is 2; `process_types` has `color_hex` and `icon_id` columns
- [x] All new tests pass; all existing tests pass (with updated mocks)

---

### EPIC-003: Command Integration

**Goal**: Wire `GetWorkItemTypeAppearancesAsync` into `twig init` and `twig refresh` to persist type appearances.

**Prerequisites**: EPIC-002 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-013 | IMPL | In `InitCommand.ExecuteAsync`, after `DetectProcessTemplateAsync` call and before saving config: call `iterationService.GetWorkItemTypeAppearancesAsync()`, map result to `List<TypeAppearanceConfig>`, assign to `config.TypeAppearances`. Add `Console.WriteLine($"  Loaded {appearances.Count} type appearance(s).");` for user feedback. | `src/Twig/Commands/InitCommand.cs` | Done |
| ITEM-014 | IMPL | In `RefreshCommand`, add `TwigConfiguration`, `TwigPaths`, and `SqliteCacheStore` to the primary constructor parameters. In `ExecuteAsync`, after the existing work item refresh logic: (1) call `iterationService.GetWorkItemTypeAppearancesAsync()`, (2) map result to `List<TypeAppearanceConfig>` and assign to `config.TypeAppearances`, (3) call `config.SaveAsync(paths.ConfigPath)`, (4) obtain the SQLite connection via `cacheStore.GetConnection()`, (5) for each appearance, execute `INSERT OR REPLACE INTO process_types (type_name, states_json, default_child_type, valid_child_types_json, color_hex, icon_id, last_synced_at) VALUES (@typeName, '[]', NULL, NULL, @colorHex, @iconId, @syncedAt)` using parameterized `SqliteCommand`. The `states_json` column uses `'[]'` as a placeholder since full process type sync is out of scope. | `src/Twig/Commands/RefreshCommand.cs` | Done |
| ITEM-015 | IMPL | Verify `Program.cs` `RefreshCommand` registration. `RefreshCommand` now has a larger constructor (`IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService`, `IIterationService`, `TwigConfiguration`, `TwigPaths`, `SqliteCacheStore`). Since `RefreshCommand` is registered via `services.AddSingleton<RefreshCommand>()` and all new dependencies (`TwigConfiguration`, `TwigPaths`, `SqliteCacheStore`) are already registered as singletons, the DI container will resolve them automatically. No `Program.cs` changes are needed unless explicit factory registration is required. | `src/Twig/Program.cs` | Done |
| ITEM-016 | TEST | Update `InitCommandTests`: add `_iterationService.GetWorkItemTypeAppearancesAsync(...)` stub returning a test list of `WorkItemTypeAppearance` objects. Add test `Init_PersistsTypeAppearances_InConfig` that verifies the saved config file contains `typeAppearances` JSON. | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Done |
| ITEM-017 | TEST | Update `RefreshCommandTests`: add `_iterationService.GetWorkItemTypeAppearancesAsync(...)` stub returning a test list of `WorkItemTypeAppearance` objects. Add test `Refresh_UpdatesTypeAppearances` that verifies appearances are fetched and config is saved. Update constructor to supply `TwigConfiguration`, `TwigPaths`, and a `SqliteCacheStore` (using `:memory:` connection string). Verify SQLite `process_types` rows contain the expected `color_hex` and `icon_id` values after refresh. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Done |

**Acceptance Criteria**:
- [x] `twig init` populates `typeAppearances` in `.twig/config`
- [x] `twig refresh` updates `typeAppearances` in `.twig/config`
- [x] `twig refresh` writes `color_hex` and `icon_id` to SQLite `process_types` rows
- [x] Console output includes type appearance count during `init`
- [x] All existing `InitCommand` and `RefreshCommand` tests pass
- [x] New tests verify type appearance persistence (config JSON for init, config JSON + SQLite for refresh)

---

## References

- [ADO REST API — Work Item Types: List (v7.1)](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/list?view=azure-devops-rest-7.1) — documents the `color`, `icon`, and `isDisabled` fields in the response
- [ADO REST API — Work Item Icons (v7.1)](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-icons?view=azure-devops-rest-7.1) — lists all available work item icon identifiers
- `docs/projects/twig-color-wiring.plan.md` — related plan for ANSI color design language; established hardcoded `GetTypeColor` that this plan's data pipeline will eventually feed
- `docs/projects/twig.prd.md` — original PRD with `process_types` table design
