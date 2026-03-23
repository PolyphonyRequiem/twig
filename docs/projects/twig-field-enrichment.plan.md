# Field Enrichment: Populate ADO Fields Across All Twig Views

| Metadata | Value |
|----------|-------|
| **Status** | DRAFT |
| **Author** | Copilot (Principal Architect) |
| **Revision** | 2 — Addresses technical review feedback |
| **Epic** | EPIC-007: Field Enrichment |

---

## Executive Summary

Twig's ADO API integration fetches **all** work item fields in every response, but `AdoResponseMapper.MapWorkItem()` only extracts 6 core properties (Type, Title, State, AssignedTo, IterationPath, AreaPath) into the `WorkItem` aggregate and discards the remaining ~80+ fields. The `WorkItem.Fields` extensible dictionary — already serialized as `fields_json` in SQLite — is never populated from API responses. This means the dynamic column infrastructure built in EPIC-004 (FieldProfileService fill-rate analysis, ColumnResolver, config-driven `display.columns`) has no data to operate on. This design proposes a process-agnostic, metadata-driven field enrichment pipeline that populates `WorkItem.Fields` during mapping, then surfaces that data across workspace, sprint, status, tree, and TUI views — activating the existing but dormant dynamic column system with zero new API calls.

---

## Background

### Current Architecture

Twig is a .NET 10 Native AOT CLI (.NET SDK 10.0.104) that manages Azure DevOps work items locally via a SQLite cache. The data pipeline is:

```
ADO REST API (v7.1) → AdoWorkItemResponse (DTO) → AdoResponseMapper.MapWorkItem() → WorkItem (aggregate)
                                                                                          ↓
                                                                                SqliteWorkItemRepository.SaveAsync()
                                                                                          ↓
                                                                                SQLite: work_items.fields_json
```

**`AdoResponseMapper.MapWorkItem()`** (lines 28-46 of `AdoResponseMapper.cs`) creates a `WorkItem` from the DTO's `Fields` dictionary. It extracts 6 core properties mapped to `WorkItem` init-only properties: `System.WorkItemType` → `Type`, `System.Title` → `Title`, `System.State` → `State`, `System.AssignedTo` → `AssignedTo`, `System.IterationPath` → `IterationPath`, `System.AreaPath` → `AreaPath`. Additionally, `Id` and `Rev` are mapped from the DTO's top-level properties. Parent ID is extracted from relation links. The remaining fields in `dto.Fields` are silently discarded.

**Call sites for `MapWorkItem()`**: The method is called from `AdoRestClient`:
- `FetchAsync()` (line 62) — single item fetch
- `FetchBatchChunkAsync()` (line 187) — batch fetch loop
- `MapWorkItemWithLinks()` (line 130) — delegates to `MapWorkItem`, called from `FetchWithLinksAsync()` (line 70)

> **Important**: `AdoIterationService` does NOT call `MapWorkItem()`. All mapping flows through `AdoRestClient`.

**`WorkItem.Fields`** is a `Dictionary<string, string?>` (with `StringComparer.OrdinalIgnoreCase`) exposed as `IReadOnlyDictionary<string, string?>` via a `ReadOnlyDictionary` wrapper. Internal mutators `SetField()` and `TryGetField()` exist. The `ReadOnlyDictionary` wraps the mutable `_fields` dictionary, so mutations via `SetField()` are immediately visible through the `Fields` property. `InternalsVisibleTo` from `Twig.Domain` grants `Twig.Infrastructure` access to `SetField()`.

**SQLite round-trip**: `SqliteWorkItemRepository.MapRow()` (lines 370-376) already deserializes `fields_json` and restores fields via `item.SetField(kvp.Key, kvp.Value)` in a foreach loop. The round-trip works correctly — but there is nothing to round-trip because the mapper never populates `Fields`.

### Prior Art — EPIC-004 Dynamic Column Infrastructure

EPIC-004 built a complete dynamic column system that is currently data-starved:

1. **`FieldProfileService.ComputeProfiles()`** — scans `WorkItem.Fields` across cached items, computes fill rates, returns `FieldProfile[]` sorted by fill rate descending. Excludes 7 core fields (System.Id, System.Title, System.State, System.WorkItemType, System.AssignedTo, System.IterationPath, System.AreaPath). Returns empty results because `Fields` is always empty.

2. **`ColumnResolver.Resolve()`** — takes profiles + field definitions + optional config overrides, applies fill-rate threshold (default 0.4), caps at 3 columns for human output, and produces `ColumnSpec[]` for table rendering. `DeriveDisplayName()` falls back to PascalCase splitting when definitions are unavailable.

3. **`SpectreTheme.CreateWorkspaceTable()`** — accepts `dynamicColumns` parameter and renders them with type-aware alignment.

4. **`SpectreRenderer.RenderWorkspaceAsync()`** (lines 104-112) — reads `item.Fields.TryGetValue(col.ReferenceName)` for each dynamic column and formats via `FormatterHelpers.FormatFieldValue()`.

5. **`SpectreRenderer.RenderWorkItemAsync()`** (lines 470-496) — already reads `System.Description`, `System.History`, and `System.Tags` from `item.Fields` for the status detail panel.

6. **`WorkspaceCommand.ResolveDynamicColumnsAsync()`** (lines 297-335) — orchestrates column resolution: loads field definitions from `IFieldDefinitionStore`, computes profiles from sprint items, passes to `ColumnResolver.Resolve()`.

All of this infrastructure exists and compiles. It simply has no data to work with.

### Field Definitions Metadata

The `field_definitions` table (synced during `twig refresh` via `FieldDefinitionSyncService`) contains metadata for every ADO field:

```sql
CREATE TABLE field_definitions (
    ref_name TEXT PRIMARY KEY,     -- e.g., "Microsoft.VSTS.Scheduling.StoryPoints"
    display_name TEXT NOT NULL,    -- e.g., "Story Points"
    data_type TEXT NOT NULL,       -- e.g., "string", "integer", "double", "dateTime", "html"
    is_read_only INTEGER NOT NULL, -- 0 or 1
    last_synced_at TEXT NOT NULL
);
```

**`FieldDefinition`** is a sealed record at `src/Twig.Domain/ValueObjects/FieldDefinition.cs` with properties: `ReferenceName`, `DisplayName`, `DataType`, `IsReadOnly`.

**`IFieldDefinitionStore`** (at `src/Twig.Domain/Interfaces/IFieldDefinitionStore.cs`) exposes `GetAllAsync()`, `GetByReferenceNameAsync()`, and `SaveBatchAsync()`. Implemented by `SqliteFieldDefinitionStore`.

This metadata is the key to process-agnostic field inclusion: instead of hardcoding `StoryPoints` or `Priority`, the mapper can use field definitions to decide which fields are worth importing.

---

## Problem Statement

1. **Silent data loss**: The ADO API returns all fields (30-100+ per work item depending on process template) but the mapper discards everything except 6 core properties. This data is already paid for in API quota and network bandwidth.

2. **Dead infrastructure**: The entire dynamic column system (FieldProfileService, ColumnResolver, dynamic columns in SpectreTheme, field display in SpectreRenderer) exists but produces empty output because `WorkItem.Fields` is never populated.

3. **Impoverished views**: `twig status` shows only 6 fields (Type, State, AssignedTo, Area, Iteration — plus Id/Title in the header). `twig tree` shows no effort/points. `twig workspace` and `twig sprint` cannot display dynamic columns. The TUI views show minimal metadata.

4. **Process assumptions risk**: Any solution that hardcodes Agile-specific fields (e.g., `Microsoft.VSTS.Scheduling.StoryPoints`) breaks for Scrum (`Microsoft.VSTS.Scheduling.Effort`), CMMI (`Microsoft.VSTS.Scheduling.Size`), and custom processes. The solution must be data-driven.

---

## Goals and Non-Goals

### Goals

| # | Goal | Measure |
|---|------|---------|
| G-1 | Populate `WorkItem.Fields` from ADO API responses during mapping | Fields dict non-empty for work items with extended fields |
| G-2 | Use field_definitions metadata for process-agnostic inclusion decisions | No hardcoded process-specific field names in inclusion logic |
| G-3 | Activate workspace/sprint dynamic columns end-to-end | `twig workspace` shows ≥1 dynamic column when fields have >40% fill rate |
| G-4 | Make `twig status` the field-richest single-item view | Status shows all populated fields with display names |
| G-5 | Show effort/points in tree view when available | Tree nodes optionally include effort after state badge |
| G-6 | Enhance TUI TreeNavigatorView with AssignedTo and dirty marker | TUI tree nodes show assignment and modification state |
| G-7 | Enhance TUI WorkItemFormView with read-only extended fields | Form shows effort, priority, tags, description below editables |

### Non-Goals

- **New API calls**: No additional API endpoints. All data comes from fields already present in work item responses.
- **Field editing for extended fields**: Extended fields are read-only display. Editing remains limited to core fields (Title, State, AssignedTo).
- **Custom field mapping configuration**: Users cannot configure which fields to import. Inclusion is automatic based on field_definitions metadata.
- **Schema migration**: `fields_json` column already exists. No DDL changes needed.
- **Process template detection**: We don't need to detect Agile vs Scrum vs CMMI. The field_definitions-driven approach is inherently process-agnostic.

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `AdoResponseMapper.MapWorkItem()` must populate `WorkItem.Fields` with non-core fields from `dto.Fields`, filtered by field_definitions metadata |
| FR-2 | Fields already mapped to core properties (System.Id, System.Rev, System.WorkItemType, System.Title, System.State, System.AssignedTo, System.IterationPath, System.AreaPath) must NOT be duplicated in Fields dict |
| FR-3 | Fields with `is_read_only = true` in field_definitions must be excluded from import, except for a curated "display-worthy readonly" allowlist (see FR-3a). Non-read-only fields must have an importable data type to be included. |
| FR-3a | Display-worthy read-only fields (Tags, Description, Created/Changed dates, board columns, Created/ChangedBy) must be imported despite `is_read_only` flag. These are universally present across all ADO process templates. |
| FR-4 | Identity-typed field values (JSON objects with `displayName`/`uniqueName` properties) must be resolved to their display name string during import. This applies to any field in the response that contains an identity object — including display-worthy read-only fields like `System.CreatedBy` and `System.ChangedBy`. The existing `ParseAssignedTo()` pattern (check for JsonElement → Object → extract displayName → fallback to uniqueName → fallback to ToString) must be generalized into a reusable `ParseIdentityOrString()` helper. |
| FR-5 | HTML field values (data_type = "html") must be stored as-is in Fields dict; HTML stripping occurs at render time (existing `StripHtmlTags` / `FormatterHelpers.FormatFieldValue`) |
| FR-6 | When field_definitions are not yet synced (empty table), the mapper must fall back to importing all non-core fields from the response (graceful degradation). Identity parsing still applies in fallback mode. |
| FR-7 | `twig workspace` and `twig sprint` must display dynamic columns via existing ColumnResolver pipeline when Fields has data |
| FR-8 | `twig status` must display all populated Fields entries in a structured detail panel with display_name labels |
| FR-9 | `twig tree` must optionally show effort/story points inline after state badge when a recognized effort field is populated |
| FR-10 | TUI `TreeNavigatorView` node display must include AssignedTo and dirty marker (`•`) |
| FR-11 | TUI `WorkItemFormView` must display read-only extended fields (effort, priority, tags, description) below editable fields |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Native AOT compatible: no reflection, all JSON serialization via source-generated `TwigJsonContext` |
| NFR-2 | No new API calls: all data sourced from existing work item responses |
| NFR-3 | Field import adds < 1ms per work item to mapping time |
| NFR-4 | Existing tests must continue to pass (Fields dict empty is still valid for items with no extended fields) |
| NFR-5 | Spectre.Console and HumanOutputFormatter rendering paths must remain in sync for parity |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ADO REST API Response                         │
│  dto.Fields: { System.Title, System.State, ..., Priority, Points… } │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                 ┌─────────────▼──────────────┐
                 │   AdoRestClient             │  ◄── MODIFIED: gains IFieldDefinitionStore
                 │   .FetchAsync()             │      dependency; loads definitions once,
                 │   .FetchBatchChunkAsync()   │      caches as lazy Dictionary, passes
                 │                             │      lookup to mapper on every call
                 └─────────────┬──────────────┘
                               │
                 ┌─────────────▼──────────────┐
                 │   FieldImportFilter         │  ◄── NEW: static pure logic (Domain)
                 │                             │
                 │   Input:  field ref_name    │
                 │   Input:  FieldDefinition?  │
                 │   Output: bool (import?)    │
                 └─────────────┬──────────────┘
                               │
                 ┌─────────────▼──────────────┐
                 │   AdoResponseMapper         │  ◄── MODIFIED: field import loop
                 │   .MapWorkItem()            │      with identity object parsing
                 │                             │
                 │   Core props → WorkItem     │
                 │   Filtered fields → Fields  │
                 └─────────────┬──────────────┘
                               │
                 ┌─────────────▼──────────────┐
                 │   WorkItem.Fields dict      │
                 │   (persisted as fields_json) │
                 └─────────────┬──────────────┘
                               │
          ┌────────────────────┼────────────────────┐
          │                    │                     │
    ┌─────▼──────┐    ┌───────▼──────┐     ┌────────▼────────┐
    │ FieldProfile│    │ ColumnResolver│     │ StatusCommand   │
    │ Service     │    │ .Resolve()   │     │ (all fields)    │
    │ (fill rates)│    │              │     │                 │
    └─────┬──────┘    └───────┬──────┘     └────────┬────────┘
          │                   │                     │
    ┌─────▼──────┐    ┌───────▼──────┐     ┌────────▼────────┐
    │ Workspace  │    │ SpectreTheme │     │ HumanOutput     │
    │ /Sprint    │    │ .CreateTable │     │ Formatter       │
    │ views      │    │ (dyn cols)   │     │ .FormatWorkItem │
    └────────────┘    └──────────────┘     └─────────────────┘
```

### Key Components

#### 1. FieldImportFilter (NEW — Domain Service)

**Location**: `src/Twig.Domain/Services/FieldImportFilter.cs`

A static, pure-logic service that determines whether a given field should be imported into `WorkItem.Fields`. Process-agnostic — no hardcoded Agile/Scrum/CMMI field names.

**Inclusion strategy**:
1. **Exclude core fields** already mapped to WorkItem properties: System.Id, System.Rev, System.WorkItemType, System.Title, System.State, System.AssignedTo, System.IterationPath, System.AreaPath (8 entries covering the 6 mapped properties plus Id and Rev)
2. **Include display-worthy read-only fields** — Tags, Description, dates, board columns, CreatedBy/ChangedBy — via a curated allowlist of system reference names universally present across all process templates
3. **Exclude all other read-only fields** — system internals, revision metadata, computed fields
4. **Include non-read-only fields with importable data types** — string, integer, double, dateTime, html, plainText
5. **Fallback when field_definitions not synced** — import all non-core fields (graceful degradation)

```csharp
public static class FieldImportFilter
{
    // 8 entries: 6 core properties + Id + Rev
    private static readonly HashSet<string> CoreFieldRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.WorkItemType",
        "System.Title", "System.State", "System.AssignedTo",
        "System.IterationPath", "System.AreaPath",
    };

    private static readonly HashSet<string> ImportableDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "integer", "double", "dateTime", "html", "plainText",
    };

    // Read-only fields that are display-worthy. These are universally present
    // across Agile, Scrum, CMMI, and Basic process templates.
    private static readonly HashSet<string> DisplayWorthyReadOnlyRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.CreatedDate", "System.ChangedDate", "System.CreatedBy",
        "System.ChangedBy", "System.Tags", "System.Description",
        "System.BoardColumn", "System.BoardColumnDone",
    };

    /// <summary>
    /// Determines whether a field should be imported into WorkItem.Fields.
    /// When <paramref name="fieldDef"/> is null (definitions not synced),
    /// all non-core fields pass through (fallback mode).
    /// </summary>
    public static bool ShouldImport(string refName, FieldDefinition? fieldDef)
    {
        if (CoreFieldRefs.Contains(refName)) return false;
        if (fieldDef is null) return true; // fallback: import everything non-core
        if (DisplayWorthyReadOnlyRefs.Contains(refName)) return true;
        if (fieldDef.IsReadOnly) return false;
        return ImportableDataTypes.Contains(fieldDef.DataType);
    }
}
```

#### 2. AdoRestClient DI Wiring Enhancement (MODIFIED)

**File**: `src/Twig.Infrastructure/Ado/AdoRestClient.cs`, `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs`

**Design Decision — Field Definition Injection Strategy**: `AdoRestClient` gains `IFieldDefinitionStore` as an optional constructor dependency. This is the cleanest approach because:

- `AdoRestClient` is the **sole caller** of `MapWorkItem()` and `MapWorkItemWithLinks()`. No other class calls these methods.
- Both `AdoRestClient` and `SqliteFieldDefinitionStore` are internal to `Twig.Infrastructure` — no cross-layer coupling.
- The field definition store reads from local SQLite (no network calls), so adding it to the network client doesn't introduce circular dependencies.
- The existing factory registration in `NetworkServiceModule` can resolve `IFieldDefinitionStore` from the service provider since it is already registered in `TwigServiceRegistration.AddTwigCoreServices()`.

**Implementation**:

```csharp
internal sealed class AdoRestClient : IAdoWorkItemService
{
    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly IFieldDefinitionStore? _fieldDefStore;

    // Lazy-loaded, cached for the process lifetime (singleton)
    private IReadOnlyDictionary<string, FieldDefinition>? _fieldDefLookup;

    public AdoRestClient(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        IFieldDefinitionStore? fieldDefinitionStore = null)
    {
        // ... existing validation ...
        _fieldDefStore = fieldDefinitionStore;
    }

    /// <summary>
    /// Loads field definitions once, caches for all subsequent mapping calls.
    /// Returns null if store is unavailable (mapper falls back to import-all).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, FieldDefinition>?> GetFieldDefLookupAsync(
        CancellationToken ct)
    {
        if (_fieldDefLookup is not null) return _fieldDefLookup;
        if (_fieldDefStore is null) return null;

        var defs = await _fieldDefStore.GetAllAsync(ct);
        if (defs.Count == 0) return null;

        var lookup = new Dictionary<string, FieldDefinition>(
            defs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
            lookup[d.ReferenceName] = d;

        _fieldDefLookup = lookup;
        return _fieldDefLookup;
    }
}
```

**DI Registration Update** (`NetworkServiceModule.cs` line 40-48):

```csharp
services.AddSingleton<IAdoWorkItemService>(sp =>
    new AdoRestClient(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<IAuthenticationProvider>(),
        cfg.Organization,
        cfg.Project,
        sp.GetService<IFieldDefinitionStore>())); // optional — null if not registered
```

Using `GetService<T>()` (not `GetRequiredService<T>()`) ensures graceful fallback when the store isn't registered (e.g., in tests or minimal configurations).

#### 3. AdoResponseMapper Enhancement (MODIFIED)

**File**: `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs`

`MapWorkItem` gains an optional `fieldDefLookup` parameter. After creating the WorkItem with core properties, it iterates `dto.Fields` and imports filtered fields. Identity objects (JSON objects with `displayName`) are resolved to strings using a generalized helper.

```csharp
public static WorkItem MapWorkItem(AdoWorkItemResponse dto,
    IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup = null)
{
    var fields = dto.Fields ?? new Dictionary<string, object?>();
    var workItem = new WorkItem { /* ...existing core mapping... */ };

    // NEW: Import non-core fields into extensible Fields dictionary
    foreach (var kvp in fields)
    {
        FieldDefinition? fieldDef = null;
        fieldDefLookup?.TryGetValue(kvp.Key, out fieldDef);
        if (!FieldImportFilter.ShouldImport(kvp.Key, fieldDef))
            continue;

        var value = ParseFieldValue(kvp.Value);
        if (value is not null)
            workItem.SetField(kvp.Key, value);
    }

    workItem.MarkSynced(dto.Rev);
    return workItem;
}

/// <summary>
/// Maps <see cref="MapWorkItemWithLinks"/> with field definition lookup support.
/// </summary>
public static (WorkItem Item, IReadOnlyList<WorkItemLink> Links) MapWorkItemWithLinks(
    AdoWorkItemResponse dto,
    IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup = null)
{
    var item = MapWorkItem(dto, fieldDefLookup);
    var links = ExtractNonHierarchyLinks(dto.Id, dto.Relations);
    return (item, links);
}
```

**Identity/String Parsing** — generalized from existing `ParseAssignedTo()`:

```csharp
/// <summary>
/// Parses a field value that may be a string, number, identity object, or other JSON element.
/// Identity objects (with displayName/uniqueName) are resolved to their display string.
/// Reuses the same pattern as <see cref="ParseAssignedTo"/> but for any field.
/// </summary>
private static string? ParseFieldValue(object? value)
{
    if (value is null) return null;

    if (value is JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object => ExtractIdentityDisplayName(element) ?? element.ToString(),
            _ => element.ToString(),
        };
    }

    return value.ToString();
}

/// <summary>
/// Extracts displayName from an identity object. Returns null if not an identity.
/// </summary>
private static string? ExtractIdentityDisplayName(JsonElement element)
{
    if (element.TryGetProperty("displayName", out var displayName))
        return displayName.GetString();
    if (element.TryGetProperty("uniqueName", out var uniqueName))
        return uniqueName.GetString();
    return null;
}
```

> **Note**: `ParseAssignedTo()` is left unchanged to avoid breaking existing core property mapping. The new `ParseFieldValue()` is used only for extended field import.

#### 4. WorkItem.ImportFields (NEW method)

**File**: `src/Twig.Domain/Aggregates/WorkItem.cs`

```csharp
/// <summary>
/// Bulk-imports field values during hydration (API mapping or SQLite deserialization).
/// Does not set dirty flag. Not for interactive edits — use <see cref="UpdateField"/> instead.
/// </summary>
internal void ImportFields(IEnumerable<KeyValuePair<string, string?>> fields)
{
    foreach (var kvp in fields)
        _fields[kvp.Key] = kvp.Value;
}
```

> **Note on `SetField()` vs `ImportFields()`**: The mapper uses `SetField()` directly (it has access via `InternalsVisibleTo`). `ImportFields()` is an internal convenience accessible via `InternalsVisibleTo` (Twig.TestKit, Twig.Infrastructure, and relevant test assemblies). `SqliteWorkItemRepository.MapRow()` already uses `SetField()` (lines 370-376) and does not need modification — the round-trip already works correctly once the mapper populates the fields.

#### 5. Status Command Enhancement

**File**: `src/Twig/Commands/StatusCommand.cs`, `HumanOutputFormatter.cs`, `SpectreRenderer.cs`

**Sync path** (`HumanOutputFormatter.FormatWorkItem`): After the existing 5 core field lines, append a section for all populated Fields with display names from field_definitions:

```
  ── Extended ──────────────────
  Priority:      2
  Story Points:  5
  Tags:          backend, auth
  Description:   Implement the login page...
  Created Date:  15d ago
  Changed Date:  2d ago
```

The formatter receives field definitions (loaded by the command) to resolve display names. Falls back to `ColumnResolver.DeriveDisplayName()` when definitions are unavailable.

**Async path** (`SpectreRenderer.RenderStatusAsync`): Extends the existing progressive loading (which already renders Description, History, Tags from `item.Fields` at lines 470-496) to include all populated fields as grid rows.

#### 6. Tree View Effort Display

**Files**: `FormatterHelpers.cs`, `HumanOutputFormatter.cs`, `SpectreRenderer.cs`

A shared helper in `FormatterHelpers` detects effort fields using suffix matching (process-agnostic):

```csharp
/// <summary>
/// Detects and returns the effort/points value from a work item's extended fields.
/// Process-agnostic: matches StoryPoints (Agile), Effort (Scrum), Size (CMMI).
/// Returns null if no effort field is populated.
/// </summary>
internal static string? GetEffortDisplay(WorkItem item)
{
    foreach (var key in item.Fields.Keys)
    {
        if (key.EndsWith("StoryPoints", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Effort", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Size", StringComparison.OrdinalIgnoreCase))
        {
            if (item.Fields.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                return val;
        }
    }
    return null;
}
```

Child node display becomes: `├── ■ #42 Implement login [Active] (5 pts)`

The config escape hatch `display.columns` allows users to override which field is shown for effort, addressing custom processes with non-standard field naming.

#### 7. TUI TreeNavigatorView Enhancement

**File**: `src/Twig.Tui/Views/TreeNavigatorView.cs`

`WorkItemNode.ToString()` (currently at line 193-197) enhanced to:
```
► ■ #42 [Task] Implement login (Active) → John Doe •
```

Where `→ John Doe` is AssignedTo (shown when non-null) and `•` is the dirty marker (shown when IsDirty).

#### 8. TUI WorkItemFormView Enhancement

**File**: `src/Twig.Tui/Views/WorkItemFormView.cs`

After the existing Area field (row 7, line 95), add read-only `TextField` elements for common extended fields populated from `WorkItem.Fields`. Fields are identified by suffix matching (same process-agnostic approach as tree effort). HTML fields are stripped for display.

### Data Flow

#### Field Import During API Fetch

```
1. AdoRestClient.FetchAsync(id) / FetchBatchChunkAsync(ids)
   → Calls GetFieldDefLookupAsync() — loads definitions once, caches
   → HTTP GET to ADO REST API
   → Deserializes AdoWorkItemResponse

2. AdoResponseMapper.MapWorkItem(dto, fieldDefLookup)
   → Creates WorkItem with 6 core properties (unchanged)
   → Iterates dto.Fields:
     → FieldImportFilter.ShouldImport(refName, fieldDef) for each field
     → ParseFieldValue(value) — handles identity objects, strings, numbers
     → workItem.SetField(key, value) for passing fields
   → Calls workItem.MarkSynced(dto.Rev)

3. Caller persists: SqliteWorkItemRepository.SaveAsync(workItem)
   → SerializeFields(item.Fields) → JSON string (~500-2000 bytes)
   → INSERT OR REPLACE into work_items.fields_json
```

#### Field Restoration During Cache Read

```
1. SqliteWorkItemRepository.MapRow(reader)
   → Reads fields_json column (line 329)
   → DeserializeFields(fieldsJson) → Dictionary (line 330)
   → item.SetField(kvp.Key, kvp.Value) for each entry (lines 370-376)
   → Fields dict is populated — NO CHANGE NEEDED here
```

#### Dynamic Column Activation

```
1. WorkspaceCommand.ResolveDynamicColumnsAsync()
   → Loads sprint items from cache (Fields NOW populated)
   → FieldProfileService.ComputeProfiles(items)
     → Returns FieldProfile[] with fill rates > 0
   → ColumnResolver.Resolve(profiles, defs, config)
     → Returns ColumnSpec[] (e.g., StoryPoints at 85%, Priority at 92%)

2. SpectreRenderer.RenderWorkspaceAsync(dynamicColumns: columns)
   → For each row, reads item.Fields[col.ReferenceName]
   → Formats via FormatterHelpers.FormatFieldValue(value, dataType)
   → Displays formatted values in dynamic columns
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **FieldImportFilter as Domain service** | Keeps inclusion logic in the domain layer, testable without infrastructure dependencies. Static class matches existing patterns (FieldProfileService, ColumnResolver). |
| **IFieldDefinitionStore injected into AdoRestClient** | AdoRestClient is the sole caller of MapWorkItem(). Both classes are internal to Twig.Infrastructure — no cross-layer coupling. The store reads local SQLite (no network I/O), so there's no circular dependency concern. Lazy caching means definitions are loaded once per process lifetime. |
| **Optional fieldDefLookup parameter on MapWorkItem** | Backward compatible — existing callers and tests that call MapWorkItem directly (without lookup) still work. The mapper falls back to importing all non-core fields when lookup is null. |
| **Generalized ParseFieldValue instead of modifying ParseAssignedTo** | ParseAssignedTo is specific to the core property mapping path. A new ParseFieldValue handles any field type (strings, numbers, identity objects) without risking regressions in existing core mapping. |
| **Suffix matching for effort display** | Process-agnostic: catches StoryPoints (Agile), Effort (Scrum), Size (CMMI) without hardcoding prefixes. Config escape hatch via `display.columns`. |
| **Display-worthy readonly allowlist** | Tags, Description, dates, CreatedBy/ChangedBy are read-only in ADO but essential for display. Small curated set (8 entries) avoids importing 50+ system revision fields while ensuring universally useful fields are available. |
| **HTML stored as-is, stripped at render** | Preserves original data fidelity; existing `StripHtmlTags` and `FormatterHelpers.FormatFieldValue` handle display-time formatting. |
| **Internal ImportFields for test convenience** | Test code accesses `ImportFields()` via `InternalsVisibleTo` (Twig.Domain grants access to Twig.Infrastructure, Twig.TestKit, and test assemblies). Internal scoping prevents external callers from bypassing dirty tracking. |
| **No schema migration** | `fields_json` column exists as `TEXT NOT NULL` defaulting to `{}`. No DDL changes needed. |

---

## Alternatives Considered

### Alternative A: Hardcoded Field List

Import a predefined list of "interesting" fields: `StoryPoints`, `Effort`, `Priority`, `Tags`, `Description`, etc.

**Pros**: Simple to implement, predictable behavior.
**Cons**: Breaks for non-standard processes, requires maintenance when new fields are added, violates process-agnostic design constraint.
**Rejected**: Does not scale to custom process templates or future ADO field additions.

### Alternative B: Import ALL Fields Unconditionally

Import every field from the API response into `WorkItem.Fields` without any filtering.

**Pros**: Zero filtering logic, guaranteed complete data.
**Cons**: ADO returns ~100+ fields per item including system revision metadata (`System.Rev`, `System.Watermark`, `System.AuthorizedDate`, etc.) and read-only computed fields (`System.NodeName`, `System.TeamProject`). This bloats `fields_json` (est. 3-5KB → 15-20KB per item), increases SQLite storage 3-4x, and pollutes fill-rate analysis with noise. FieldProfileService would surface `System.Rev` (100% fill rate) as a dynamic column.
**Rejected**: Data noise degrades the fill-rate analysis that drives dynamic columns.

### Alternative C: Fetch Field Definitions On-Demand in Mapper

Have the mapper query `IFieldDefinitionStore` during mapping.

**Pros**: Always up-to-date definitions.
**Cons**: Introduces async I/O into a currently synchronous pure-mapping method. Creates a dependency on a repository from the anti-corruption layer. Breaks clean architecture.
**Rejected**: Mapper should remain pure. Callers provide the lookup.

### Alternative D: Pass Field Definitions Through IAdoWorkItemService Interface

Add field definition parameters to `FetchAsync()`, `FetchBatchAsync()`, etc. on the `IAdoWorkItemService` interface, requiring all callers to provide definitions.

**Pros**: No hidden state in AdoRestClient. Explicit data flow.
**Cons**: Changes the public interface used by 10+ call sites across commands and sync services. Callers would need to resolve `IFieldDefinitionStore` themselves, creating coupling in the command layer that currently doesn't exist. Forces test doubles to accept the new parameter.
**Rejected**: High ripple effect for marginal benefit. The store injection approach (chosen) keeps the change localized to `AdoRestClient` and `NetworkServiceModule`.

### Alternative E: Higher-Level Orchestration Layer

Introduce a `WorkItemFetchOrchestrator` that wraps `IAdoWorkItemService` and enriches responses with field definitions.

**Pros**: Clean separation of concerns. AdoRestClient stays purely network-focused.
**Cons**: Adds an additional abstraction layer and DI registration. All callers of `IAdoWorkItemService` would need to switch to the orchestrator. Over-engineering for a single cross-cutting concern.
**Rejected**: The added complexity doesn't justify the marginal architectural purity gain. AdoRestClient already lives in Infrastructure alongside the field definition store.

---

## Dependencies

### External Dependencies
- **Spectre.Console** — already in use, no new dependency
- **Terminal.Gui** — already in use for TUI, no new dependency
- **System.Text.Json** — source-gen context already configured

### Internal Dependencies
- **FieldDefinitionSyncService** must have run at least once for optimal filtering. Without field_definitions, the mapper falls back to importing all non-core fields (acceptable degradation).
- **`IFieldDefinitionStore` registration** — already registered by `TwigServiceRegistration.AddTwigCoreServices()` as `SqliteFieldDefinitionStore`. Available in the DI container before `NetworkServiceModule` runs.

### Sequencing Constraints
- Epic 1 (field import) must complete before Epic 2 (view enhancements) can be verified end-to-end.
- Field definitions must be synced (`twig refresh`) before filtered import works optimally.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `AdoRestClient` | Modified: new `IFieldDefinitionStore?` constructor parameter, lazy field def loading, pass lookup to all MapWorkItem calls |
| `NetworkServiceModule` | Modified: pass `IFieldDefinitionStore` in AdoRestClient factory registration |
| `AdoResponseMapper` | Modified: new field import loop in `MapWorkItem()` and `MapWorkItemWithLinks()`, new `ParseFieldValue()` and `ExtractIdentityDisplayName()` helpers |
| `WorkItem` | Modified: new `ImportFields()` internal method for bulk hydration |
| `HumanOutputFormatter` | Modified: extended fields in `FormatWorkItem`, effort in `FormatTree` |
| `SpectreRenderer` | Modified: extended fields in `RenderStatusAsync`, effort in tree nodes |
| `StatusCommand` | Modified: load field definitions, pass to formatter for extended display |
| `FormatterHelpers` | Modified: new `GetEffortDisplay()` helper |
| `WorkItemFormView` (TUI) | Modified: additional read-only fields |
| `TreeNavigatorView` (TUI) | Modified: `WorkItemNode.ToString()` enhancement |
| `WorkItemBuilder` (TestKit) | Modified: add `WithField()` fluent method for test data |
| `FieldImportFilter` | **New**: domain service for field inclusion logic |

### Backward Compatibility

- **fields_json round-trip**: Items saved before this change have `fields_json = "{}"`. After this change, newly synced items have populated fields. Old items display with empty dynamic columns until re-synced via `twig refresh`. This is acceptable.
- **MapWorkItem() signature**: The new `fieldDefLookup` parameter is optional (default `null`). Existing direct callers in tests continue to work — they get fallback behavior (import all non-core fields).
- **Existing tests**: All pass because `Fields` being empty is still valid. New tests verify populated Fields.
- **JSON output format**: Dynamic columns appear in JSON output when populated. This is additive — existing JSON consumers see new fields.
- **SqliteWorkItemRepository.MapRow()**: No changes needed — already restores fields via `SetField()` (lines 370-376).

### Performance Implications

- **Mapping overhead**: ~30-50 additional dictionary insertions per work item (filtered from ~100 raw fields). Negligible: <0.5ms per item.
- **Field definition loading**: One async call to `GetAllAsync()` per process lifetime (lazy-cached in `AdoRestClient`). Typically <5ms from SQLite.
- **SQLite storage**: `fields_json` grows from ~2 bytes (`{}`) to ~500-2000 bytes per item. For a typical sprint of 50 items, total increase is ~50-100KB. Negligible.
- **Fill-rate computation**: `FieldProfileService.ComputeProfiles` iterates `Fields` per item. With ~40 fields × 50 items = 2000 iterations. Negligible.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Field definitions not synced on first use | Medium | Low | Fallback: import all non-core fields when definitions are empty. First `twig refresh` syncs definitions. |
| Large HTML fields (descriptions) bloat SQLite | Low | Low | HTML is typically 1-5KB. Total per-sprint increase < 250KB. Monitor in integration tests. |
| Dynamic columns show noisy fields (System.Rev) | Medium | Medium | FieldImportFilter excludes read-only fields. DisplayWorthyReadOnly allowlist is conservative (8 entries). |
| Effort suffix matching misses custom fields | Low | Low | Custom processes with non-standard effort fields won't show in tree. Users can configure `display.columns` to override. |
| Breaking change in Fields dict (non-empty where empty expected) | Low | Medium | Existing tests verify empty Fields is valid. No consumer assumes empty. |
| Identity field parsing fallback to ToString() | Low | Low | Defensive: worst case shows `System.Text.Json.JsonElement` string representation. Same pattern used by existing `ParseAssignedTo()`. |
| Lazy field definition cache goes stale during long-running TUI session | Low | Low | Definitions rarely change. Cache is per-process; TUI restart or `twig refresh` reloads. |

---

## Open Questions — RESOLVED

1. **[RESOLVED]** ~~Should the display-worthy readonly allowlist be user-configurable via `config.json`, or is the hardcoded set sufficient for V1?~~
   **Decision**: Configurable via `config.json`, with intelligent defaults discovered from field_definitions metadata (at init or render time). Ship smart defaults so it works out of the box.

2. **[RESOLVED]** ~~For tree view effort display, should we show the field's display name (e.g., "5 Story Points") or just the numeric value with a generic label (e.g., "5 pts")?~~
   **Decision**: Just the numeric value with a short generic label (e.g., `5 pts`). Tree real estate is precious.

3. **[RESOLVED]** ~~Should `twig status` show ALL populated fields or only the top N by field_definitions ordering?~~
   **Decision**: Show top 10 fields by default, respect terminal height, cap at 50 maximum. Configurable via `display.statusMaxFields` (default: 10, max: 50).

4. **[RESOLVED]** ~~The `WorkItemBuilder` in TestKit needs a `WithField()` method. Should it also support `WithFields(Dictionary<string, string?>)` for bulk test setup?~~
   **Decision**: Provide both `WithField(key, value)` and `WithFields(dict)` for convenience.

---

## Implementation Phases

### Phase 1: Core Field Import (Epic 1)
**Exit Criteria**: `AdoResponseMapper.MapWorkItem()` populates `WorkItem.Fields` from API responses. Field definitions are loaded in AdoRestClient and passed to mapper. SQLite round-trip works. Existing tests pass. New unit tests cover FieldImportFilter and mapper enhancement.

### Phase 2: CLI View Enhancements (Epic 2)
**Exit Criteria**: `twig workspace`, `twig sprint`, `twig status`, `twig tree` all display enriched field data. HumanOutputFormatter and SpectreRenderer paths produce equivalent output. Effort display helper works across process templates.

### Phase 3: TUI Enhancements (Epic 3)
**Exit Criteria**: TreeNavigatorView shows AssignedTo + dirty marker. WorkItemFormView shows extended read-only fields.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/FieldImportFilter.cs` | Field inclusion logic — determines which ADO fields to import |
| `tests/Twig.Domain.Tests/Services/FieldImportFilterTests.cs` | Unit tests for FieldImportFilter |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Add `ImportFields()` internal method for bulk hydration |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Add field import loop in `MapWorkItem()`, forward `fieldDefLookup` in `MapWorkItemWithLinks()`, add `ParseFieldValue()` and `ExtractIdentityDisplayName()` helpers |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Add `IFieldDefinitionStore?` constructor parameter, lazy `GetFieldDefLookupAsync()`, pass lookup to mapper in `FetchAsync()`, `FetchWithLinksAsync()`, `FetchBatchChunkAsync()` |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Pass `IFieldDefinitionStore` to AdoRestClient factory |
| `src/Twig/Commands/StatusCommand.cs` | Load field definitions, pass to formatter for extended display |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Enhance `FormatWorkItem()` with extended fields section, enhance `FormatTree()` with effort display |
| `src/Twig/Formatters/FormatterHelpers.cs` | Add `GetEffortDisplay(WorkItem)` helper |
| `src/Twig/Rendering/SpectreRenderer.cs` | Enhance `RenderStatusAsync()` with all populated fields, enhance tree node labels with effort |
| `src/Twig.Tui/Views/TreeNavigatorView.cs` | Enhance `WorkItemNode.ToString()` with AssignedTo and dirty marker |
| `src/Twig.Tui/Views/WorkItemFormView.cs` | Add read-only extended fields below editable fields |
| `tests/Twig.TestKit/WorkItemBuilder.cs` | Add `WithField()` / `WithFields()` fluent methods |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Tests for field import, identity parsing, core field exclusion, fallback behavior |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Tests for extended field display |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Tests for enriched status output |
| `tests/Twig.Tui.Tests/TreeNavigatorViewTests.cs` | Tests for enhanced node display |
| `tests/Twig.Tui.Tests/WorkItemFormViewTests.cs` | Tests for extended form fields |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### Epic 1: Core Field Import Pipeline — DONE

**Goal**: Populate `WorkItem.Fields` from ADO API responses using data-driven field definitions metadata. Wire field definitions into AdoRestClient. Validate SQLite round-trip.

**Prerequisites**: None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `FieldImportFilter` static service with `ShouldImport()` method — CoreFieldRefs exclusion (8 entries: 6 core props + Id + Rev), ImportableDataTypes allowlist, DisplayWorthyReadOnlyRefs allowlist (8 entries), null fieldDef fallback | `src/Twig.Domain/Services/FieldImportFilter.cs` | DONE |
| E1-T2 | TEST | Unit tests for FieldImportFilter: core field exclusion, read-only exclusion, display-worthy readonly inclusion, data type filtering, null fieldDef fallback (import-all), case insensitivity | `tests/Twig.Domain.Tests/Services/FieldImportFilterTests.cs` | DONE |
| E1-T3 | IMPL | Add `ImportFields(IEnumerable<KeyValuePair<string, string?>>)` internal method to `WorkItem` — bulk hydration without dirty flag, for mapper and test convenience (internal via InternalsVisibleTo) | `src/Twig.Domain/Aggregates/WorkItem.cs` | DONE |
| E1-T4 | IMPL | Add `ParseFieldValue(object?)` and `ExtractIdentityDisplayName(JsonElement)` private helpers to `AdoResponseMapper`. Generalize identity object parsing (displayName → uniqueName → ToString fallback) for any field value. | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | DONE |
| E1-T5 | IMPL | Enhance `AdoResponseMapper.MapWorkItem()` with optional `IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup` parameter. Add field import loop: iterate dto.Fields, call FieldImportFilter.ShouldImport(), call ParseFieldValue(), call workItem.SetField(). Update `MapWorkItemWithLinks()` to forward the lookup parameter. | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | DONE |
| E1-T6 | IMPL | Add `IFieldDefinitionStore?` optional constructor parameter to `AdoRestClient`. Add private `GetFieldDefLookupAsync()` with lazy caching. Update `FetchAsync()` (line 62), `FetchWithLinksAsync()` (line 70), and `FetchBatchChunkAsync()` (line 187) to load lookup and pass to `MapWorkItem()` / `MapWorkItemWithLinks()`. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | DONE |
| E1-T7 | IMPL | Update `NetworkServiceModule.AddTwigNetworkServices()` AdoRestClient factory (line 40-48) to pass `sp.GetService<IFieldDefinitionStore>()` as the new constructor parameter. | `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | DONE |
| E1-T8 | TEST | Mapper tests: fields populated from response, core fields excluded, read-only filtered, null definitions fallback (import all non-core), identity object fields resolved to displayName, HTML fields stored as-is | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | DONE |
| E1-T9 | TEST | SQLite round-trip test: save work item with populated Fields, read back via repository, verify Fields dict contains expected entries | `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | DONE |
| E1-T10 | IMPL | Add `WithField(string, string?)` and `WithFields(Dictionary<string, string?>)` to `WorkItemBuilder` test kit — calls `item.SetField()` (accessible via InternalsVisibleTo from Twig.TestKit) | `tests/Twig.TestKit/WorkItemBuilder.cs` | DONE |
| E1-T11 | TEST | Run full test suite, verify all existing tests pass without modification | All test projects | DONE |

**Acceptance Criteria**:
- [x] `AdoResponseMapper.MapWorkItem()` populates `WorkItem.Fields` with non-core, filtered fields
- [x] `FieldImportFilter` passes all unit tests including fallback behavior
- [x] Identity objects in field values are resolved to display names
- [x] `AdoRestClient` loads field definitions lazily and passes to mapper
- [x] `NetworkServiceModule` wires `IFieldDefinitionStore` into `AdoRestClient`
- [x] SQLite `fields_json` correctly round-trips populated Fields dictionary
- [x] All existing tests pass without modification
- [x] `WorkItemBuilder.WithField()` available for test authoring

---

### Epic 2: CLI View Enhancements

**Goal**: Surface enriched field data in workspace, sprint, status, and tree CLI views. Keep HumanOutputFormatter and SpectreRenderer in sync. Add process-agnostic effort display helper.

**Prerequisites**: Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Verify workspace/sprint dynamic columns work end-to-end with populated Fields — the existing ColumnResolver + SpectreRenderer pipeline should auto-activate via `WorkspaceCommand.ResolveDynamicColumnsAsync()`; fix any gaps in the Spectre or HumanOutputFormatter table rendering paths | `WorkspaceCommand.cs`, `SpectreRenderer.cs` | TO DO |
| E2-T2 | TEST | Integration test: workspace command with items having populated Fields shows dynamic columns in output | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | TO DO |
| E2-T3 | IMPL | Enhance `HumanOutputFormatter.FormatWorkItem()` to append populated Fields with display_name labels after core fields. Accept optional `IReadOnlyList<FieldDefinition>?` parameter for display name resolution, falling back to `ColumnResolver.DeriveDisplayName()`. Format values using `FormatterHelpers.FormatFieldValue()` with increased `maxWidth` for status display. | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| E2-T4 | IMPL | Enhance `SpectreRenderer.RenderStatusAsync()` to iterate all populated `item.Fields` entries (beyond the existing Description/History/Tags) and add them as labeled rows in the detail grid. Use field definitions for display names. | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E2-T5 | IMPL | Load field definitions in `StatusCommand`, pass to both sync path (`FormatWorkItem`) and async path (`RenderStatusAsync`) for display_name resolution. `StatusCommand` already has access to formatter and renderer. | `src/Twig/Commands/StatusCommand.cs` | TO DO |
| E2-T6 | TEST | Status command tests: verify extended fields appear in both sync and async output with correct display names | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | TO DO |
| E2-T7 | IMPL | Add `GetEffortDisplay(WorkItem)` helper to `FormatterHelpers` using suffix matching (StoryPoints/Effort/Size) for process-agnostic effort detection. Returns formatted string or null. | `src/Twig/Formatters/FormatterHelpers.cs` | TO DO |
| E2-T8 | IMPL | Enhance `HumanOutputFormatter.FormatTree()` child nodes to show effort inline after state badge when `GetEffortDisplay()` returns non-null | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| E2-T9 | IMPL | Enhance `SpectreRenderer.RenderTreeAsync()` child node labels (line 269) to show effort inline after state badge using `GetEffortDisplay()` | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E2-T10 | TEST | Tree view tests: verify effort appears inline when available, absent when not, across both formatters | `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs`, `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig workspace` shows dynamic columns when items have >40% fill rate on extended fields
- [ ] `twig status` displays all populated Fields with display_name labels
- [ ] `twig tree` shows effort/points inline after state badge when available
- [ ] HumanOutputFormatter and SpectreRenderer produce equivalent enriched output
- [ ] JSON and Minimal formatters handle extended fields gracefully (no crashes)

---

### Epic 3: TUI View Enhancements

**Goal**: Enhance Terminal.Gui views with enriched field data — AssignedTo/dirty in tree, read-only fields in form.

**Prerequisites**: Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Enhance `WorkItemNode.ToString()` (line 193-197) to append `→ {AssignedTo}` when `WorkItem.AssignedTo` is non-null, and `•` dirty marker when `WorkItem.IsDirty` is true. Current format: `{marker}{badge} #{Id} [{Type}] {Title} ({State})`. New format: `{marker}{badge} #{Id} [{Type}] {Title} ({State}) → {AssignedTo} •` | `src/Twig.Tui/Views/TreeNavigatorView.cs` | TO DO |
| E3-T2 | TEST | TreeNavigatorView tests: verify node string includes AssignedTo and dirty marker when present, absent when not applicable | `tests/Twig.Tui.Tests/TreeNavigatorViewTests.cs` | TO DO |
| E3-T3 | IMPL | Add read-only `TextField` elements to `WorkItemFormView` after the Area field (line 95) for common extended fields: effort/points, priority, tags, description. Populate from `WorkItem.Fields` via suffix matching in `LoadWorkItem()`. Use `SpectreRenderer.StripHtmlTags()` for HTML fields. | `src/Twig.Tui/Views/WorkItemFormView.cs` | TO DO |
| E3-T4 | TEST | WorkItemFormView tests: verify extended fields display with values, update on LoadWorkItem, read-only behavior, HTML stripping | `tests/Twig.Tui.Tests/WorkItemFormViewTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] TUI tree nodes show `→ AssignedTo •` when assigned and dirty
- [ ] TUI form shows read-only effort, priority, tags, description fields populated from Fields dict
- [ ] Extended fields update correctly when switching between work items in the tree
- [ ] All existing TUI tests pass without modification

---

## References

- `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` — Current mapper implementation (lines 28-46)
- `src/Twig.Infrastructure/Ado/AdoRestClient.cs` — All MapWorkItem call sites: FetchAsync (line 62), FetchWithLinksAsync (line 70), FetchBatchChunkAsync (line 187)
- `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` — AdoRestClient DI registration (lines 40-48)
- `src/Twig.Domain/Aggregates/WorkItem.cs` — Domain aggregate with Fields dict, SetField() internal mutator
- `src/Twig.Domain/ValueObjects/FieldDefinition.cs` — Field metadata sealed record
- `src/Twig.Domain/Interfaces/IFieldDefinitionStore.cs` — Field definition store interface
- `src/Twig.Domain/Services/FieldProfileService.cs` — Fill-rate analysis (EPIC-004)
- `src/Twig.Domain/Services/ColumnResolver.cs` — Dynamic column resolution (EPIC-004)
- `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` — fields_json serialization (lines 286-297), MapRow field restoration (lines 370-376)
- `src/Twig/Commands/WorkspaceCommand.cs` — ResolveDynamicColumnsAsync (lines 297-335)
- `src/Twig/Rendering/SpectreRenderer.cs` — Dynamic column rendering (lines 104-112), extended fields in status (lines 470-496)
- `src/Twig/Formatters/FormatterHelpers.cs` — FormatFieldValue with HTML stripping, type-aware formatting
- `src/Twig/Formatters/HumanOutputFormatter.cs` — FormatWorkItem (lines 59-75), FormatTree
- `src/Twig.Tui/Views/TreeNavigatorView.cs` — WorkItemNode.ToString() (lines 193-197)
- `src/Twig.Tui/Views/WorkItemFormView.cs` — LoadWorkItem (lines 123-162)
- `tests/Twig.TestKit/WorkItemBuilder.cs` — Test builder (no WithField yet)
- [Azure DevOps Work Item Fields REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/fields)
- [Azure DevOps Work Items REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/get-work-item)
