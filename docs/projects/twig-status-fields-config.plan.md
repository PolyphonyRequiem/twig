# Status Fields Configuration: User-Controlled Field Selection for `twig status`

| Metadata | Value |
|----------|-------|
| **Status** | DRAFT |
| **Author** | Copilot (Principal Architect) |
| **Revision** | 3 — Addressed technical review feedback (score 88/100): importability filtering, core field set disambiguation, routing risk clarification |
| **Epic** | EPIC-010: Status Fields Configuration |

---

## Executive Summary

After EPIC-007 (Field Enrichment), `WorkItem.Fields` now contains 20–80 imported ADO fields per work item, but the `twig status` rendering paths (`SpectreRenderer.AddExtendedFieldRows()` and `HumanOutputFormatter.GetExtendedFields()`) display the first 10 non-empty, non-core fields in arbitrary dictionary insertion order with no prioritization or user control. This design introduces a `twig config status-fields` command that generates a human-editable field configuration file listing all known field definitions, opens it in the user's editor (reusing the existing `EditorLauncher` infrastructure), and persists the user's selections to `.twig/status-fields`. Subsequent `twig status` invocations read this file to control which fields appear, in what order. The design requires a new domain service (`StatusFieldsConfig`), a new CLI command (`ConfigStatusFieldsCommand`), a new `TwigPaths` property, and surgical modifications to the two existing rendering paths.

---

## Background

### Current Architecture

Twig is a .NET 10 Native AOT CLI for Azure DevOps work item management. The `twig status` command shows a detailed view of the active work item via two rendering paths:

1. **Spectre (live) path** — `SpectreRenderer.RenderStatusAsync()` (line 379) renders a panel with core fields (Type, State, Assigned, Area, Iteration) followed by extended fields via `AddExtendedFieldRows()` (line 517). This method iterates `item.Fields` in dictionary order, skips empty values and core fields, and caps output at **10 fields**.

2. **Human (sync) path** — `HumanOutputFormatter.FormatWorkItem()` (line 64) renders core fields then calls `GetExtendedFields()` (line 921), which uses the same dictionary-order iteration with **no explicit cap**.

Both paths share the same limitation: **no user control over which fields appear or their display order**.

### Prior Art

- **`EditCommand`** (line 12 of `EditCommand.cs`) — Precedent for editor-open-parse workflow using `IEditorLauncher`. Generates initial content, launches editor, parses changes on return.
- **`ColumnResolver`** (line 22 of `ColumnResolver.cs`) — Precedent for config-driven field selection in workspace/sprint views via `configuredColumns` parameter and `display.columns.workspace`/`display.columns.sprint` config keys.
- **`FieldProfileService`** — Precedent for static domain service performing pure computation on field data.
- **`IFieldDefinitionStore.GetAllAsync()`** — Returns all `FieldDefinition` records (ReferenceName, DisplayName, DataType, IsReadOnly) cached from the ADO fields API.
- **`EditorLauncher`** — Fully implemented editor resolution chain ($VISUAL → $EDITOR → $GIT_EDITOR → git config core.editor) with 5-minute timeout, exit code handling, and unchanged-content-means-abort semantics. Writes to `.twig/EDIT_MSG`, waits for editor exit, returns content or `null`.

### What Changed

EPIC-007 activated the field enrichment pipeline, populating `WorkItem.Fields` with 20–80 fields per item via `FieldImportFilter.ShouldImport()` during `AdoResponseMapper.MapWorkItem()`. This surfaced the display problem: users now see a wall of arbitrarily-ordered fields with no way to prioritize what matters to them. The existing `display.columns.workspace` config controls workspace/sprint table columns but does **not** affect `twig status` detail view, which uses a completely separate rendering path.

---

## Problem Statement

1. **No prioritization**: `twig status` shows fields in dictionary insertion order (determined by JSON property ordering from ADO's API response), meaning important fields like Priority, Story Points, or Tags can appear after trivial fields like ChangedDate or BoardColumnDone.
2. **No user control**: Users cannot choose which fields to show or hide. The only option is the hard-coded 10-field cap (Spectre path).
3. **No ordering control**: Users cannot reorder fields to match their workflow.
4. **No persistence**: Even if filtering logic were added, there is no mechanism to persist field display preferences for the status view across invocations.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G-1 | Introduce `twig config status-fields` that generates a human-editable configuration file listing all known field definitions, opens it in the user's editor, and persists selections to `.twig/status-fields`. |
| G-2 | `twig status` reads `.twig/status-fields` (when present) to control which extended fields are displayed and in what order. |
| G-3 | On first run, provide intelligent defaults by starring commonly useful fields (effort/points, priority, severity, tags, date fields). |
| G-4 | On subsequent runs, merge newly discovered fields (appended unmarked at bottom) while preserving existing selections and ordering. |
| G-5 | Round-trip fidelity — `Generate() → Parse() → Generate()` produces stable output. |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG-1 | Controlling workspace/sprint table columns (already handled by `display.columns.workspace`/`display.columns.sprint`). |
| NG-2 | Inline field editing or field value manipulation. |
| NG-3 | Per-work-item-type field profiles (all items use the same status-fields config). |
| NG-4 | TUI view integration — the TUI's `WorkItemFormView` has hardcoded fields and is out of scope. |
| NG-5 | Controlling JSON output format (`--output json` shows all fields regardless). |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `twig config status-fields` generates a text file from `IFieldDefinitionStore.GetAllAsync()`, **filtered to only importable fields** (via `FieldImportFilter.ShouldImport()` logic), listing field definitions with display name, reference name, and data type. Fields that cannot appear in `WorkItem.Fields` at runtime are excluded from the config file. |
| FR-2 | The file uses a `*` prefix to indicate included fields. Lines without `*` are excluded. Line order determines display order. |
| FR-3 | Comment lines (starting with `#`) at the top explain the format and are ignored during parsing. |
| FR-4 | The file is opened in the user's editor via `IEditorLauncher.LaunchAsync()`. |
| FR-5 | On save, the file is written to `.twig/status-fields` (workspace-level, not context-scoped). |
| FR-6 | On first run (no existing file), intelligent defaults star fields whose display name contains 'effort', 'points', 'priority', 'severity', 'tags', plus all `dateTime`-typed fields. |
| FR-7 | On subsequent runs, the existing file is loaded, new fields are appended unmarked at bottom, and removed fields are dropped. Existing selections and order are preserved. |
| FR-8 | `SpectreRenderer.AddExtendedFieldRows()` reads status-field entries (passed in from `StatusCommand`) to filter and order extended fields. Falls back to current behavior when entries are null. |
| FR-9 | `HumanOutputFormatter.GetExtendedFields()` reads status-field entries with the same semantics as FR-8. |
| FR-10 | If the user does not save (editor aborted or content unchanged), no file is written and a cancellation message is shown. |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Native AOT compatible — no reflection, no dynamic code generation. |
| NFR-2 | Process-agnostic — no assumptions about Agile/Scrum/CMMI field names in default-selection logic. Uses display name heuristics, not hardcoded reference names. |
| NFR-3 | File format is human-readable and hand-editable outside the editor workflow. |
| NFR-4 | Parsing is tolerant — malformed lines are ignored, unknown reference names are silently dropped during rendering. |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────┐
│ twig config status-fields    │
│  (ConfigStatusFieldsCommand) │
└──────────┬───────────────────┘
           │ 1. GetAllAsync()
           ▼
┌──────────────────────────┐     ┌──────────────────────────┐
│ IFieldDefinitionStore    │     │ StatusFieldsConfig       │
│ (SQLite-backed)          │     │ (Domain Service)         │
└──────────────────────────┘     │                          │
           │                     │ • Generate(defs, exist?) │
           │ field definitions   │ • Parse(content)         │
           │ (filtered thru      │ • IsDefaultStarred()     │
           │  ShouldImport)      │ • IsImportable()         │
           └────────────────────►│                          │
                                 └──────────┬───────────────┘
                                            │ generated content
                                            ▼
                                 ┌──────────────────────────┐
                                 │ IEditorLauncher          │
                                 │ (existing — unchanged)   │
                                 └──────────┬───────────────┘
                                            │ edited content
                                            ▼
                                 ┌──────────────────────────┐
                                 │ .twig/status-fields      │
                                 │ (persisted file)         │
                                 └──────────┬───────────────┘
                                            │ read on status
                                            ▼
                          ┌─────────────────────────────────────┐
                          │ SpectreRenderer / HumanOutput-      │
                          │ Formatter (rendering paths)         │
                          └─────────────────────────────────────┘
```

### Key Components

#### 1. `StatusFieldsConfig` (Domain Service)

**Location**: `src/Twig.Domain/Services/StatusFieldsConfig.cs`

A pure, static service with no I/O dependencies — following the established pattern of `FieldProfileService`, `FieldImportFilter`, and `ColumnResolver`.

```csharp
namespace Twig.Domain.Services;

/// <summary>
/// Generates, parses, and merges the status-fields configuration file that controls
/// which extended fields appear in 'twig status' output and in what order.
/// </summary>
public static class StatusFieldsConfig
{
    /// <summary>
    /// Core fields excluded from the config file. This is the 9-field superset matching
    /// SpectreRenderer.CoreFields and HumanOutputFormatter.CoreFieldPrefixes.
    /// Defined here because StatusFieldsConfig (in Twig.Domain) cannot reference the
    /// renderer-private sets in Twig assembly.
    /// </summary>
    internal static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.WorkItemType",
        "System.Title", "System.State", "System.AssignedTo",
        "System.IterationPath", "System.AreaPath", "System.TeamProject",
    };

    /// <summary>
    /// Returns true if a field definition represents a field that can appear in
    /// WorkItem.Fields at runtime. Delegates to FieldImportFilter.ShouldImport()
    /// and additionally excludes core fields using the renderer-matching 9-field set.
    /// Fields that fail this check are omitted from the generated config file
    /// because they would be dead entries — users could star them but they would
    /// never appear in twig status output.
    /// </summary>
    public static bool IsImportable(FieldDefinition def)
        => !CoreFields.Contains(def.ReferenceName)
           && FieldImportFilter.ShouldImport(def.ReferenceName, def);

    /// <summary>
    /// Generates file content from field definitions, optionally merging with
    /// existing configuration to preserve user selections and ordering.
    /// Definitions are filtered through IsImportable() before inclusion.
    /// </summary>
    public static string Generate(
        IReadOnlyList<FieldDefinition> definitions,
        string? existingContent = null);

    /// <summary>
    /// Parses file content into an ordered list of (refName, isIncluded) entries.
    /// Ignores comment lines, blank lines, and malformed lines.
    /// </summary>
    public static IReadOnlyList<StatusFieldEntry> Parse(string content);

    /// <summary>
    /// Returns true if a field definition should be starred by default based on
    /// display name heuristics and data type.
    /// </summary>
    public static bool IsDefaultStarred(FieldDefinition def);
}
```

**Responsibilities**:

- **`Generate(definitions, existingContent)`**: First filters `definitions` through `IsImportable()` to exclude core fields and non-importable fields (read-only non-display-worthy, non-importable data types like boolean/treePath/history). When `existingContent` is `null`, produces a fresh file with comment header + field lines. Fields matching `IsDefaultStarred()` are prefixed with `*`. Fields sorted alphabetically by display name within each group (starred first, then unstarred). When `existingContent` is provided, parses it, preserves existing entries in their original order with their `*` state, appends new definitions (not in existing) unmarked at bottom, and omits definitions no longer present in the store or no longer importable.

- **`IsImportable(def)`**: Returns `true` if the field is not in the 9-field `CoreFields` set AND passes `FieldImportFilter.ShouldImport(def.ReferenceName, def)`. This ensures the config file only lists fields that can actually appear in `WorkItem.Fields` at runtime. `FieldImportFilter.ShouldImport()` handles: (a) excluding its own 8-field `CoreFieldRefs` set, (b) allowing `DisplayWorthyReadOnlyRefs` through regardless of read-only status, (c) excluding other read-only fields, (d) requiring data type to be in `ImportableDataTypes` (string, integer, double, dateTime, html, plainText). The additional `CoreFields` check catches `System.TeamProject` — which passes `ShouldImport()` (it's not in `FieldImportFilter.CoreFieldRefs`) but is silently skipped by both renderers.

- **`Parse(content)`**: Splits on newlines. Skips lines starting with `#` and blank lines. For each remaining line: checks if it starts with `*` (after optional whitespace) → `IsIncluded = true`. Extracts reference name from `(...)` parenthesized suffix via simple string search. Returns `IReadOnlyList<StatusFieldEntry>`.

- **`IsDefaultStarred(def)`**: Returns `true` if `def.DisplayName` contains any of: "effort", "points", "priority", "severity", "tags" (case-insensitive), OR if `def.DataType` is "dateTime" (case-insensitive).

#### 2. `StatusFieldEntry` (Value Object)

**Location**: `src/Twig.Domain/ValueObjects/StatusFieldEntry.cs`

```csharp
namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents a single entry from the parsed status-fields configuration file.
/// </summary>
public readonly record struct StatusFieldEntry(string ReferenceName, bool IsIncluded);
```

Minimal value object. Using `readonly record struct` for allocation-free AOT-compatible usage — consistent with the codebase's use of record types for value objects.

#### 3. `ConfigStatusFieldsCommand` (CLI Command)

**Location**: `src/Twig/Commands/ConfigStatusFieldsCommand.cs`

```csharp
namespace Twig.Commands;

public sealed class ConfigStatusFieldsCommand(
    IFieldDefinitionStore fieldDefinitionStore,
    IEditorLauncher editorLauncher,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory)
{
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}
```

**Execution flow**:

1. Load all field definitions via `fieldDefinitionStore.GetAllAsync(ct)`.
2. Guard: if definitions are empty, print error "No field definitions cached. Run 'twig refresh' first." and return 1.
3. Load existing `.twig/status-fields` content: `File.Exists(paths.StatusFieldsPath) ? await File.ReadAllTextAsync(paths.StatusFieldsPath, ct) : null`.
4. Generate editor content: `StatusFieldsConfig.Generate(definitions, existingContent)`.
5. Launch editor: `var edited = await editorLauncher.LaunchAsync(content, ct)`.
6. If `edited is null`: print "Configuration cancelled." and return 0.
7. Write edited content: `await File.WriteAllTextAsync(paths.StatusFieldsPath, edited, ct)`.
8. Parse to count: `var entries = StatusFieldsConfig.Parse(edited); var count = entries.Count(e => e.IsIncluded)`.
9. Print success: "Saved {count} field(s) to .twig/status-fields."
10. Return 0.

#### 4. `TwigPaths.StatusFieldsPath` Property

**Location**: Modify `src/Twig.Infrastructure/Config/TwigPaths.cs`

```csharp
/// <summary>Path to the status-fields config file: <c>.twig/status-fields</c>.</summary>
public string StatusFieldsPath => Path.Combine(TwigDir, "status-fields");
```

This is workspace-level (in `.twig/` root, not context-scoped under `{org}/{project}/`) because field display preferences are user workflow choices, not org/project-specific data.

#### 5. Rendering Path Modifications

##### `IAsyncRenderer.RenderStatusAsync()`

Add optional parameter:

```csharp
Task RenderStatusAsync(
    Func<Task<WorkItem?>> getItem,
    Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
    CancellationToken ct,
    IReadOnlyList<FieldDefinition>? fieldDefinitions = null,
    IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null);  // NEW
```

##### `SpectreRenderer.AddExtendedFieldRows()`

Modified signature:

```csharp
private static void AddExtendedFieldRows(
    Grid grid, WorkItem item,
    IReadOnlyList<FieldDefinition>? fieldDefinitions,
    IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null)  // NEW
```

**Logic when `statusFieldEntries` is not null**:

```
Build defLookup from fieldDefinitions (existing code).
For each entry in statusFieldEntries where entry.IsIncluded:
  - Look up entry.ReferenceName in item.Fields → value
  - If value is null/whitespace → skip
  - Resolve displayName from defLookup or DeriveDisplayName()
  - Format and add row (existing formatting logic)
No cap on count — user explicitly chose these fields.
```

**Logic when `statusFieldEntries` is null**: Existing behavior unchanged (iterate dictionary, skip core/empty, cap at 10).

##### `HumanOutputFormatter.GetExtendedFields()`

Modified signature:

```csharp
private static List<(string DisplayName, string Value)> GetExtendedFields(
    WorkItem item,
    Dictionary<string, FieldDefinition> defLookup,
    IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null)  // NEW
```

Same branching logic as SpectreRenderer: if entries provided, iterate included entries in order; otherwise fall back to existing behavior.

##### `HumanOutputFormatter.FormatWorkItem()` overload

Add an optional parameter to the existing 3-parameter overload (rather than a new overload):

```csharp
// Overload 1: unchanged — delegates to overload 2
public string FormatWorkItem(WorkItem item, bool showDirty)
    => FormatWorkItem(item, showDirty, fieldDefinitions: null);

// Overload 2: MODIFIED — add optional configuredStatusFields parameter
public string FormatWorkItem(WorkItem item, bool showDirty,
    IReadOnlyList<FieldDefinition>? fieldDefinitions,
    IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null)  // NEW optional param
```

This approach avoids a third overload. The existing call site in `StatusCommand.ExecuteAsync()` (line 136: `humanFmt.FormatWorkItem(item, showDirty: true, syncFieldDefs)`) continues to compile unchanged because `statusFieldEntries` defaults to `null`. Only `StatusCommand` needs to be updated to pass the new parameter when config is available.

##### `StatusCommand` changes

**Constructor modification (REQUIRED):** `StatusCommand`'s primary constructor (line 16 of `StatusCommand.cs`) does **not** currently include `TwigPaths`. It must be added as a required parameter before the optional parameters:

```csharp
public sealed class StatusCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinator syncCoordinator,
    TwigPaths paths,                              // NEW — required, inserted before optional params
    RenderingPipelineFactory? pipelineFactory = null,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    TextWriter? stderr = null)
```

This constructor change **also requires** updating the DI factory lambda in `CommandRegistrationModule.cs` (see [DI Registration Changes](#di-registration-changes) below) and all test constructor calls in `StatusCommandTests.cs`.

**In `ExecuteAsync()`**, after loading `fieldDefs`:

```csharp
// Load status-fields config (best-effort)
IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null;
if (File.Exists(paths.StatusFieldsPath))
{
    try
    {
        var configContent = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
        statusFieldEntries = StatusFieldsConfig.Parse(configContent);
    }
    catch { /* best-effort — fall back to default behavior */ }
}
```

Then pass `statusFieldEntries` to both rendering paths.

### Data Flow

#### `twig config status-fields` (configure)

```
User runs command
  → ConfigStatusFieldsCommand.ExecuteAsync()
    → IFieldDefinitionStore.GetAllAsync() → List<FieldDefinition>
    → File.ReadAllText(TwigPaths.StatusFieldsPath) → existingContent (or null)
    → StatusFieldsConfig.Generate(definitions, existingContent)
      → Filter definitions through IsImportable() (excludes 9 core + non-importable)
      → Generate file content with only importable fields
    → IEditorLauncher.LaunchAsync(content) → editedContent (or null)
    → [if null] print "Configuration cancelled." → return 0
    → [if not null] File.WriteAllText(TwigPaths.StatusFieldsPath, editedContent)
    → print "Saved N field(s) to .twig/status-fields."
    → return 0
```

#### `twig status` (consume)

```
User runs command
  → StatusCommand.ExecuteAsync()
    → [existing: resolve active item, load field definitions]
    → File.Exists(paths.StatusFieldsPath)?      // paths = injected TwigPaths
      → [yes] File.ReadAllText() → StatusFieldsConfig.Parse() → entries
      → [no]  entries = null
    → [Spectre path] renderer.RenderStatusAsync(..., statusFieldEntries: entries)
      → AddExtendedFieldRows(grid, item, fieldDefs, statusFieldEntries: entries)
    → [Human path] humanFmt.FormatWorkItem(item, showDirty, fieldDefs, statusFieldEntries: entries)
      → GetExtendedFields(item, defLookup, statusFieldEntries: entries)
```

### File Format

```
# twig status-fields configuration
# Lines starting with '#' are comments and ignored.
# Prefix a line with '*' to include that field in 'twig status' output.
# The order of lines determines the display order.
# To reset, delete this file and run 'twig config status-fields' again.
#
# Format: [*] Display Name              (reference.name)           [data_type]
#

* Priority                    (Microsoft.VSTS.Common.Priority)           [integer]
* Story Points                (Microsoft.VSTS.Scheduling.StoryPoints)    [double]
* Tags                        (System.Tags)                              [string]
* Severity                    (Microsoft.VSTS.Common.Severity)           [string]
* Created Date                (System.CreatedDate)                       [dateTime]
* Changed Date                (System.ChangedDate)                       [dateTime]
  Value Area                  (Microsoft.VSTS.Common.ValueArea)          [string]
  Board Column                (System.BoardColumn)                       [string]
  Description                 (System.Description)                       [html]
  Created By                  (System.CreatedBy)                         [string]
  Changed By                  (System.ChangedBy)                         [string]
```

**Parsing rules**:

1. Lines starting with `#` → skip (comment).
2. Blank/whitespace-only lines → skip.
3. Lines starting with `*` (after optional leading whitespace) → `IsIncluded = true`. Extract reference name from `(...)`.
4. Lines not starting with `*` → `IsIncluded = false`. Extract reference name from `(...)`.
5. If no `(...)` found on a line → skip (malformed).
6. Reference name is the canonical identifier; display name and `[data_type]` in the file are informational only (regenerated from field definitions on next edit/merge).

### Design Decisions

#### DD-1: Workspace-level file, not context-scoped

The status-fields file lives at `.twig/status-fields` rather than `.twig/{org}/{project}/status-fields`.

**Rationale**: Field display preferences are personal workflow choices, not org/project-specific data. Users working across multiple orgs/projects within the same repo likely want the same field visibility. This also avoids complicating the `TwigPaths` context-scoping logic.

#### DD-2: Separate command, not `twig config` key

`twig config status-fields` is a distinct subcommand routed via `[Command("config status-fields")]`, not a `twig config <key> <value>` operation.

**Rationale**: `ConfigCommand` operates on simple key-value pairs with `GetValue()`/`SetValue()` semantics. Status fields configuration requires an interactive editor workflow with a multi-line, structured file format. It would be a poor fit for the existing `ConfigCommand` pattern. The multi-word command approach is already established by `hooks install`, `hooks uninstall`, `stash pop`, and `flow-*` commands.

#### DD-3: Static domain service, not interface

`StatusFieldsConfig` is a `public static class` (like `FieldProfileService`, `FieldImportFilter`, `ColumnResolver`) rather than an interface-based service.

**Rationale**: It performs pure computation with no I/O dependencies, no state, and no need for DI-based testing seams. The static pattern is well-established in this codebase for such services.

#### DD-4: Pass entries through StatusCommand, not read in renderers

The renderers (`SpectreRenderer`, `HumanOutputFormatter`) receive parsed status field entries as parameters rather than reading the file themselves.

**Rationale**: Keeps renderers pure (no file system access), maintains the existing pattern where `StatusCommand` orchestrates data loading, and makes testing straightforward (inject entries directly).

#### DD-5: File content written directly, not through EditorLauncher

The command writes to `.twig/status-fields` after EditorLauncher returns, rather than having EditorLauncher write to the final path.

**Rationale**: EditorLauncher's contract is "give initial content, get back edited content or `null`." It uses `.twig/EDIT_MSG` as a temp file and cleans it up in its `finally` block (line 98-99 of EditorLauncher.cs). The persistence path is the command's responsibility.

#### DD-6: EditorLauncher unchanged-content = abort

The existing `EditorLauncher` returns `null` when content is unchanged (line 91-92), treating it as an abort. This is the desired behavior for this command.

**Rationale**: Consistent with `EditCommand` behavior and git commit message editing conventions. If the user opens the file and closes without changes, we don't create/update the status-fields file.

#### DD-7: Core fields and non-importable fields excluded from the config file

Core fields are excluded from the generated file — they are always shown as dedicated rows in the status panel. Additionally, non-importable fields (those that `FieldImportFilter.ShouldImport()` rejects) are excluded because they never appear in `WorkItem.Fields` and would be dead entries in the config.

**Core field set**: `StatusFieldsConfig` defines its own `CoreFields` constant with the **9-field superset** matching `SpectreRenderer.CoreFields` and `HumanOutputFormatter.CoreFieldPrefixes`:

```
System.Id, System.Rev, System.WorkItemType, System.Title, System.State,
System.AssignedTo, System.IterationPath, System.AreaPath, System.TeamProject
```

This set is defined locally in `StatusFieldsConfig` (in `Twig.Domain`) because the renderer sets are `private static` in the `Twig` assembly and cannot be referenced cross-assembly. The 9-field set is chosen over the 8-field `FieldImportFilter.CoreFieldRefs` (which omits `System.TeamProject`) and the 7-field `FieldProfileService.CoreFields` (which omits both `System.Rev` and `System.TeamProject`). Using any smaller set would produce dead config entries — fields the user can star but that renderers silently skip.

**Importability filtering**: Beyond core field exclusion, `Generate()` also excludes fields that fail `FieldImportFilter.ShouldImport()` — specifically: (a) read-only fields not in `DisplayWorthyReadOnlyRefs` (e.g., internal system fields), (b) fields with non-importable data types (boolean, treePath, history, etc.). Without this filter, `IFieldDefinitionStore.GetAllAsync()` returns ~200+ field definitions from the ADO fields API, but only ~20-80 pass the import filter into `WorkItem.Fields`. Listing all ~200+ would present users with ~100+ fields that can never appear in output, creating a confusing UX when starred fields are silently ignored.

#### DD-8: No cap on starred fields

When the user provides explicit selections via the config file, there is no artificial cap on the number of displayed fields (unlike the current 10-field cap in `AddExtendedFieldRows`).

**Rationale**: The 10-field cap was a safeguard against uncontrolled dictionary-order output. With explicit user selection, the user has already decided what's important. If they star 30 fields, that's their choice.

#### DD-9: Explicit `[Command("config")]` on existing Config method

The existing `Config` method in `Program.cs` (line 332) uses implicit method-name routing — it has no `[Command]` attribute. When adding `[Command("config status-fields")]` on the new method, **both** routes should use explicit `[Command]` attributes.

**Rationale**: The `stash`/`stash pop` precedent (lines 348-355) and `hooks install`/`hooks uninstall` precedent (lines 398-404) both use explicit `[Command]` attributes on all related methods. Mixing implicit and explicit routing between a parent command and its sub-command creates ambiguity — ConsoleAppFramework's longest-prefix matching may or may not correctly disambiguate `twig config status-fields` (explicit) from `twig config` (implicit with `[Argument] string key`). This risk is amplified by a subtle difference from the `stash`/`stash pop` precedent: `Stash` takes an _optional_ `[Argument] string? message = null`, while `Config` takes a _required_ `[Argument] string key`. ConsoleAppFramework's route disambiguation may behave differently with required vs. optional positional arguments — a required argument creates stronger binding that could cause the framework to consume `status-fields` as the `key` argument rather than treating it as a sub-command prefix. Making both explicit eliminates this risk entirely. This is a safe change — the route remains `"config"` — and costs only one added attribute line.

#### DD-10: `configuredStatusFields` as optional parameter on existing overloads

The `HumanOutputFormatter.FormatWorkItem(WorkItem, bool, IReadOnlyList<FieldDefinition>?)` overload gains `configuredStatusFields` as an **optional parameter with `null` default**, rather than introducing a third overload.

**Rationale**: Adding an optional parameter preserves backward compatibility — all existing call sites (including the 1-parameter overload that delegates to the 3-parameter one, and `StatusCommand`'s sync path at line 136) continue to compile unchanged. A third overload would require choosing which overload callers target, adding confusion. The optional parameter propagates cleanly through to the private `GetExtendedFields()` helper.

---

## Alternatives Considered

### Alternative A: Extend existing `twig config` with special key handling

Add `status-fields` as a special key in `ConfigCommand` that triggers the editor workflow instead of get/set.

| Aspect | Assessment |
|--------|------------|
| **Pros** | No new command class, reuses existing routing |
| **Cons** | Breaks the simple key-value contract of `ConfigCommand`. The `Config([Argument] string key, [Argument] string? value = null, ...)` signature would need special-case routing. Muddies separation of concerns. |

**Decision**: Rejected. A separate command is cleaner and follows established multi-word command precedent.

### Alternative B: Store config in `twig.json` (TwigConfiguration)

Store the list of selected field reference names as a `display.statusFields` array in `TwigConfiguration`, similar to `display.columns.workspace`.

| Aspect | Assessment |
|--------|------------|
| **Pros** | All config in one place. No separate file to manage. |
| **Cons** | Loses the human-editable text file UX, which is central to the design. A JSON array of reference names is much less user-friendly than the annotated text format with display names, comments, and `*` toggles. |

**Decision**: Rejected. The editable text file format is the defining feature of this design.

### Alternative C: Read config in renderers directly (inject TwigPaths into renderers)

Have `SpectreRenderer` and `HumanOutputFormatter` read `.twig/status-fields` directly via an injected `TwigPaths` dependency.

| Aspect | Assessment |
|--------|------------|
| **Pros** | Simpler call sites — no need to thread entries through StatusCommand. |
| **Cons** | Adds file I/O to renderers that are currently pure. `SpectreRenderer` is `internal sealed` and constructed via DI factory in `RenderingServiceModule`. `HumanOutputFormatter` has no DI dependencies currently — it's created directly in `OutputFormatterFactory`. Adding `TwigPaths` would cascade through constructors and factories. |

**Decision**: Rejected. Threading entries through StatusCommand is a few extra lines but preserves renderer purity and avoids DI cascades.

---

## Dependencies

### Internal Dependencies

- **`IFieldDefinitionStore`** — Must be populated (via `twig refresh` or `twig init`) before `twig config status-fields` can list fields. The command handles empty definitions gracefully.
- **`IEditorLauncher` / `EditorLauncher`** — Reused as-is, no modifications needed. Interface at `src/Twig.Domain/Interfaces/IEditorLauncher.cs` (line 7); implementation at `src/Twig/Commands/EditorLauncher.cs` (line 25: `LaunchAsync`, line 107: `ResolveEditor`).
- **`TwigPaths`** — Modified to add `StatusFieldsPath` property (1-line addition).
- **`StatusCommand`** — Modified to load and pass config entries. `TwigPaths` is **not** currently in its constructor (line 16 of `StatusCommand.cs`) and must be added. This also requires updating the DI factory in `CommandRegistrationModule.cs` (line 51) and test constructors in `StatusCommandTests.cs` (line 57).

### External Dependencies

None. No new NuGet packages, no new API calls, no new process launches beyond the existing editor infrastructure.

### Sequencing Constraints

- Field definitions must exist in the cache (user must have run `twig init` + `twig refresh` at least once). The command handles the empty-definitions case by printing a helpful error.

---

## Impact Analysis

### Components Affected

| Component | Type of Change | Estimated Size |
|-----------|----------------|----------------|
| `TwigPaths` | Add computed property | 1 line |
| `StatusCommand` | Add `TwigPaths` constructor param; load config, pass to renderers | ~15 lines |
| `IAsyncRenderer` | Add optional `statusFieldEntries` parameter to `RenderStatusAsync` | 1 line |
| `SpectreRenderer.AddExtendedFieldRows()` | Add parameter, conditional branch for configured vs. unconfigured | ~20 lines |
| `SpectreRenderer.RenderStatusAsync()` | Thread parameter to `AddExtendedFieldRows` | ~3 lines |
| `HumanOutputFormatter.FormatWorkItem()` | Add optional `configuredStatusFields` parameter to existing 3-param overload; thread to `GetExtendedFields` | ~5 lines |
| `HumanOutputFormatter.GetExtendedFields()` | Add optional `configuredStatusFields` parameter; conditional branch for configured iteration | ~20 lines |
| `CommandRegistrationModule` | **(1)** Register `ConfigStatusFieldsCommand` (new factory lambda); **(2)** Update `StatusCommand` factory (line 51) to inject `sp.GetRequiredService<TwigPaths>()` as new required parameter | ~15 lines |
| `TwigCommands` (Program.cs) | Add `[Command("config status-fields")]` method; add `[Command("config")]` to existing `Config` method | ~5 lines |
| `GroupedHelp` (Program.cs) | Add help entry | 1 line |
| `StatusCommandTests` | Update constructor calls to include `TwigPaths` parameter across all test setup | ~5 lines |

### Backward Compatibility

- **Fully backward compatible**. When `.twig/status-fields` does not exist, all rendering behavior is identical to the current implementation.
- The `twig config <key> [value]` command is unchanged — adding `[Command("config")]` to the existing `Config` method makes its routing explicit (matching the `stash`/`stash pop` precedent) without changing the route itself. ConsoleAppFramework's multi-word `[Command("config status-fields")]` route takes precedence when the subcommand matches.
- No file format changes to existing files.
- `IAsyncRenderer.RenderStatusAsync()` signature change is additive (new optional parameter with `null` default).
- `HumanOutputFormatter.FormatWorkItem()` signature change is additive (new optional parameter with `null` default on existing 3-parameter overload).

### Performance

Negligible. The status-fields file is tiny (<5KB). File read is a single `File.ReadAllText()` call on every `twig status` invocation. Parse is a linear scan of ~100 lines. No measurable impact.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ConsoleAppFramework routes `twig config status-fields` to existing `Config` command instead of new `ConfigStatusFields` command | Medium | High | The existing `Config` method in `Program.cs` (line 332) uses **implicit method-name routing** (no `[Command]` attribute). The new `ConfigStatusFields` method will use `[Command("config status-fields")]`. Following the established `stash`/`stash pop` and `hooks install`/`hooks uninstall` precedent — where **both** the parent and sub-commands use explicit `[Command]` attributes — add `[Command("config")]` to the existing `Config` method. This eliminates any routing ambiguity between implicit and explicit route matching. **Note**: There is a subtle distinction from the `stash`/`stash pop` precedent — `Stash` takes an _optional_ `[Argument] string? message = null`, while `Config` takes a _required_ `[Argument] string key`. ConsoleAppFramework's longest-prefix route disambiguation may behave differently with required vs. optional positional arguments; required arguments create stronger binding that could interfere with sub-command matching. Making both routes explicit via `[Command]` attributes eliminates this risk. Verify with a smoke test that `twig config display.hints` still routes correctly AND that `twig config status-fields` reaches the new command. |
| Field definitions store is empty on first run | Medium | Low | Command detects empty definitions and prints: "No field definitions cached. Run 'twig refresh' first." Returns exit code 1. |
| User hand-edits `.twig/status-fields` with malformed content | Medium | Low | Parser is tolerant — skips malformed lines. Worst case: no fields shown (all lines unparseable). User can delete file and re-run command. |
| EditorLauncher 5-minute timeout expires during editing | Low | Low | Existing behavior: EditorLauncher kills the editor process and returns `null`. Command treats as cancellation. Timeout is generous for a config file edit. |

---

## Open Questions

1. **[Low] RESOLVED** — Include `[data_type]` annotation. Decision: Yes, include it — helps users distinguish field types.

2. **[Low] RESOLVED** — `--reset` flag. Decision: Not in v1. Users can delete `.twig/status-fields` manually.

3. **[Low] RESOLVED** — Cap on starred fields. Decision: No cap — user chose them deliberately.

---

## Implementation Phases

### Phase 1: Domain Service + Value Object

**Exit criteria**: `StatusFieldsConfig.Generate()`, `Parse()`, and `IsDefaultStarred()` work correctly with full test coverage. Round-trip fidelity verified.

### Phase 2: CLI Command + TwigPaths

**Exit criteria**: `twig config status-fields` opens the editor, generates correct content, persists to `.twig/status-fields`, and handles edge cases (empty store, cancelled edit).

### Phase 3: Rendering Integration

**Exit criteria**: `twig status` reads `.twig/status-fields` when present and uses it to control field selection and ordering in both Spectre and Human output paths. Falls back correctly when file is absent. All existing tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/StatusFieldsConfig.cs` | Pure domain service: Generate/Parse/IsDefaultStarred for status-fields config file |
| `src/Twig.Domain/ValueObjects/StatusFieldEntry.cs` | Value object representing a parsed (refName, isIncluded) entry |
| `src/Twig/Commands/ConfigStatusFieldsCommand.cs` | CLI command implementing `twig config status-fields` |
| `tests/Twig.Domain.Tests/Services/StatusFieldsConfigTests.cs` | Unit tests for Generate/Parse/merge/defaults |
| `tests/Twig.Cli.Tests/Commands/ConfigStatusFieldsCommandTests.cs` | Unit tests for command flow, edge cases |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigPaths.cs` | Add `StatusFieldsPath` computed property (1 line) |
| `src/Twig/Commands/StatusCommand.cs` | Add `TwigPaths` constructor param (required, before optional params); load status-fields config in `ExecuteAsync`; pass entries to renderers |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Add optional `statusFieldEntries` parameter to `RenderStatusAsync` signature |
| `src/Twig/Rendering/SpectreRenderer.cs` | Modify `RenderStatusAsync()` and `AddExtendedFieldRows()` to accept and use status field entries |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add optional `statusFieldEntries` parameter to existing `FormatWorkItem` 3-param overload; modify `GetExtendedFields()` to accept and use entries |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | **(1)** Register `ConfigStatusFieldsCommand` with DI factory; **(2)** Update `StatusCommand` factory (line 51) to inject `sp.GetRequiredService<TwigPaths>()` |
| `src/Twig/Program.cs` | Add `[Command("config status-fields")]` route; add `[Command("config")]` attribute to existing `Config` method; update `GroupedHelp` |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Update `StatusCommand` constructor calls in test setup to include `TwigPaths` parameter |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Add tests for configured field selection/ordering via `statusFieldEntries` parameter |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### Epic 1: Domain Service and Value Object

**Status**: DONE

**Goal**: Implement the pure domain logic for generating, parsing, and merging status-fields configuration content.

**Prerequisites**: None.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `StatusFieldEntry` readonly record struct with `ReferenceName` (string) and `IsIncluded` (bool) properties. | `src/Twig.Domain/ValueObjects/StatusFieldEntry.cs` | DONE |
| E1-T2 | IMPL | Create `StatusFieldsConfig` static class with `CoreFields` internal constant (9-field `HashSet<string>` matching `SpectreRenderer.CoreFields`/`HumanOutputFormatter.CoreFieldPrefixes`), `IsImportable(FieldDefinition)` method (returns `!CoreFields.Contains(def.ReferenceName) && FieldImportFilter.ShouldImport(def.ReferenceName, def)`), and `IsDefaultStarred(FieldDefinition)` method (returns true if DisplayName contains 'effort', 'points', 'priority', 'severity', or 'tags' case-insensitive, or if DataType is 'dateTime'). | `src/Twig.Domain/Services/StatusFieldsConfig.cs` | DONE |
| E1-T3 | IMPL | Implement `StatusFieldsConfig.Generate(definitions, existingContent)`. First, filter `definitions` through `IsImportable()` — this excludes the 9-field `CoreFields` set (matching `SpectreRenderer.CoreFields`: System.Id, System.Rev, System.WorkItemType, System.Title, System.State, System.AssignedTo, System.IterationPath, System.AreaPath, System.TeamProject) AND fields that fail `FieldImportFilter.ShouldImport()` (read-only non-display-worthy fields, non-importable data types like boolean/treePath/history). When `existingContent` is null: produce comment header, then starred defaults first (sorted by display name), then unstarred (sorted by display name). Each line formatted as: `[* ] {DisplayName padded}  ({ReferenceName})  [{DataType}]`. When `existingContent` is provided: parse existing, preserve order/selections, append new importable fields unmarked at bottom, drop removed or no-longer-importable fields. | `src/Twig.Domain/Services/StatusFieldsConfig.cs` | DONE |
| E1-T4 | IMPL | Implement `StatusFieldsConfig.Parse(content)`. Split on newlines. Skip comment (`#`) and blank lines. For each remaining line: detect leading `*` → `IsIncluded=true`. Extract reference name from `(...)` parenthesized text. Return `IReadOnlyList<StatusFieldEntry>`. | `src/Twig.Domain/Services/StatusFieldsConfig.cs` | DONE |
| E1-T5 | TEST | Unit tests for `StatusFieldsConfig`: (1) `Generate` from scratch produces valid content with correct defaults starred, (2) `Parse` extracts correct entries from generated content, (3) Generate→Parse round-trip produces expected entries, (4) `IsDefaultStarred` correctly identifies effort/points/priority/severity/tags/dateTime fields and rejects others, (5) Merge preserves existing order and selections while appending new fields and dropping removed ones, (6) `Parse` handles malformed lines (no parens, empty lines, comment-only content) gracefully, (7) Core fields (all 9: Id, Rev, WorkItemType, Title, State, AssignedTo, IterationPath, AreaPath, TeamProject) are excluded from generated output, (8) `IsImportable` excludes read-only non-display-worthy fields (e.g., `System.Watermark`), (9) `IsImportable` excludes non-importable data types (boolean, treePath, history), (10) `IsImportable` allows display-worthy read-only fields through (CreatedDate, Tags, etc.), (11) `IsImportable` excludes System.TeamProject even though it passes FieldImportFilter.ShouldImport, (12) Generated config file only contains importable fields — non-importable definitions from GetAllAsync are filtered out. | `tests/Twig.Domain.Tests/Services/StatusFieldsConfigTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `StatusFieldsConfig.Generate(definitions)` produces well-formatted content with comment header and intelligent defaults
- [x] `StatusFieldsConfig.Generate()` filters definitions through `IsImportable()` — non-importable fields are excluded from output
- [x] `StatusFieldsConfig.Parse(content)` correctly extracts all (refName, isIncluded) entries
- [x] Generate→Parse round-trip is stable
- [x] Merge logic preserves existing order, appends new fields, drops removed fields
- [x] Core fields (9-field superset including System.TeamProject) are excluded
- [x] Non-importable fields (wrong data type, read-only non-display-worthy) are excluded
- [x] All unit tests pass

---

### Epic 2: TwigPaths and CLI Command — DONE

**Goal**: Implement the `twig config status-fields` command and wire it into the CLI routing.

**Prerequisites**: Epic 1 (StatusFieldsConfig domain service).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Add `StatusFieldsPath` computed property to `TwigPaths`: `public string StatusFieldsPath => Path.Combine(TwigDir, "status-fields");` | `src/Twig.Infrastructure/Config/TwigPaths.cs` | DONE |
| E2-T2 | IMPL | Create `ConfigStatusFieldsCommand` with primary constructor DI pattern accepting `IFieldDefinitionStore`, `IEditorLauncher`, `TwigPaths`, `OutputFormatterFactory`. Implement `ExecuteAsync()`: load definitions, guard empty, load existing file, generate content, launch editor, handle cancel, write file, print success with count. | `src/Twig/Commands/ConfigStatusFieldsCommand.cs` | DONE |
| E2-T3 | IMPL | Register `ConfigStatusFieldsCommand` in `CommandRegistrationModule.AddCoreCommands()` with DI factory lambda injecting `IFieldDefinitionStore`, `IEditorLauncher`, `TwigPaths`, `OutputFormatterFactory`. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DONE |
| E2-T4 | IMPL | **(1)** Add `[Command("config")]` attribute to the existing `Config` method in `TwigCommands` (line 332 of `Program.cs`) to make routing explicit — required for disambiguation with the new subcommand (see DD-9). **(2)** Add `[Command("config status-fields")]` method `ConfigStatusFields` in `TwigCommands` that resolves `ConfigStatusFieldsCommand` from `IServiceProvider` and calls `ExecuteAsync()`. **(3)** Add entry to `GroupedHelp.Show()` under the System section: `config status-fields  Configure which fields appear in status view.` | `src/Twig/Program.cs` | DONE |
| E2-T5 | TEST | Unit tests for `ConfigStatusFieldsCommand`: (1) Success path — mock `IFieldDefinitionStore` returns definitions, mock `IEditorLauncher` returns edited content → file written to correct path, exit 0, (2) Empty definitions → prints error, returns 1, (3) Editor cancellation (launcher returns null) → prints message, returns 0, no file written, (4) Existing file → merged content passed to editor, (5) Verify correct `StatusFieldsPath` used. | `tests/Twig.Cli.Tests/Commands/ConfigStatusFieldsCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `twig config status-fields` generates correct content and opens editor
- [x] Cancelled edit prints message and returns 0
- [x] Successful edit writes to `.twig/status-fields`
- [x] Empty field definitions store returns helpful error
- [x] Merge with existing file works correctly
- [x] Command appears in `twig --help` grouped output
- [x] All unit tests pass

---

### Epic 3: Rendering Integration

**Goal**: Modify `twig status` to read the status-fields config and use it to control field display in both rendering paths.

**Prerequisites**: Epic 1 (StatusFieldsConfig), Epic 2 (TwigPaths.StatusFieldsPath).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Add optional `IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null` parameter to `IAsyncRenderer.RenderStatusAsync()`. | `src/Twig/Rendering/IAsyncRenderer.cs` | TO DO |
| E3-T2 | IMPL | Modify `SpectreRenderer.RenderStatusAsync()` to accept `statusFieldEntries` and forward to `AddExtendedFieldRows()`. Modify `AddExtendedFieldRows()`: when entries provided, iterate included entries in order looking up values from `item.Fields` with display name resolution from `defLookup`. When null, preserve current behavior (dictionary iteration with 10-field cap). | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E3-T3 | IMPL | Add optional `IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null` parameter to the existing `FormatWorkItem(WorkItem, bool, IReadOnlyList<FieldDefinition>?)` overload in `HumanOutputFormatter`. Modify `GetExtendedFields()` to accept optional `IReadOnlyList<StatusFieldEntry>?`: when provided, iterate included entries in order; when null, preserve current behavior. | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| E3-T4 | IMPL | **StatusCommand constructor change:** Add `TwigPaths paths` as a required parameter to `StatusCommand`'s primary constructor (insert after `SyncCoordinator`, before the optional `RenderingPipelineFactory?`). This is the integration point for reading `.twig/status-fields`. | `src/Twig/Commands/StatusCommand.cs` | TO DO |
| E3-T5 | IMPL | **StatusCommand DI registration update:** Update the `StatusCommand` factory lambda in `CommandRegistrationModule.AddCoreCommands()` (line 51) to inject `sp.GetRequiredService<TwigPaths>()` matching the new constructor parameter position. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| E3-T6 | IMPL | **StatusCommand config loading:** In `StatusCommand.ExecuteAsync()`, add logic after field definition loading to: (a) check `File.Exists(paths.StatusFieldsPath)`, (b) read and parse via `StatusFieldsConfig.Parse()`, (c) pass `statusFieldEntries` to both `renderer.RenderStatusAsync()` (Spectre path) and `humanFmt.FormatWorkItem()` (sync path). Handle file read errors with try/catch fallback to `null`. | `src/Twig/Commands/StatusCommand.cs` | TO DO |
| E3-T7 | TEST | **Update StatusCommandTests:** Modify test constructor (line 57 of `StatusCommandTests.cs`) to include `TwigPaths` parameter. Create a temp `.twig` directory for test paths. Verify all existing tests still pass with the updated constructor. | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | TO DO |
| E3-T8 | TEST | Tests for rendering integration: (1) `AddExtendedFieldRows` with status entries shows only starred fields in correct order, (2) `AddExtendedFieldRows` without entries preserves current behavior (10-field cap), (3) `GetExtendedFields` with status entries filters correctly, (4) Unknown reference names in entries are silently skipped, (5) All-unstarred entries results in no extended fields shown. (6) `StatusCommand` with config file present passes entries to renderer. | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` (extend), `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` (extend) | TO DO |

**Acceptance Criteria**:
- `StatusCommand` constructor includes `TwigPaths` and compiles correctly
- `CommandRegistrationModule` injects `TwigPaths` into `StatusCommand` factory
- `twig status` with `.twig/status-fields` present shows only starred fields in file order
- `twig status` without `.twig/status-fields` shows current behavior (no regression)
- Unknown reference names in config are silently ignored
- Empty fields (no value on the work item) are skipped even if starred
- Both Spectre (live) and Human (sync) rendering paths respect the config
- All existing status command tests continue to pass after constructor update
- All new tests pass

---

## References

- [EPIC-007: Field Enrichment Plan](twig-field-enrichment.plan.md) — Prior work that populated `WorkItem.Fields`
- [`EditorLauncher.cs`](../../src/Twig/Commands/EditorLauncher.cs) — Existing editor integration (reused unchanged)
- [`EditCommand.cs`](../../src/Twig/Commands/EditCommand.cs) — Precedent for editor-open-parse workflow
- [`ColumnResolver.cs`](../../src/Twig.Domain/Services/ColumnResolver.cs) — Precedent for config-driven field selection
- [`FieldProfileService.cs`](../../src/Twig.Domain/Services/FieldProfileService.cs) — Precedent for static domain service pattern
- [`FieldImportFilter.cs`](../../src/Twig.Domain/Services/FieldImportFilter.cs) — Core field exclusion list
- [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework) — Command routing with `[Command]` attribute
