# Twig Seed Foundation — Local-First Seeds + Subcommand Restructure

**Plan:** 1 of 3 — Seed Foundation  
**Status:** Draft  
**Revision:** 2 — Revised with deeper codebase analysis, corrected seed counter safety, refined editor format specification, and expanded implementation details.

---

## Executive Summary

This plan transforms the Twig seed subsystem from a fire-and-forget ADO push mechanism into a local-first drafting system with a structured subcommand tree. Today, `twig seed "title"` immediately creates a work item in Azure DevOps and caches the result locally. This plan removes the immediate ADO push, keeping seeds as local SQLite drafts with negative sentinel IDs until explicitly published (deferred to Plan 3). Concurrently, it restructures the single `twig seed` entry point into a subcommand tree (`new`, `view`, `edit`, `discard`) with editor-driven field population, a seed dashboard, and a discard workflow. The domain model changes are designed to accommodate future Plans 2 (virtual links, chain builder) and 3 (publish, validate, reconcile).

---

## Background

### Current Architecture

The seed lifecycle today is a straight pipeline:

1. **User invokes** `twig seed "title"` — routed via `TwigCommands.Seed()` in `Program.cs` (line 296) to `SeedCommand.ExecuteAsync()`.
2. **SeedFactory.Create()** validates parent/child rules against `ProcessConfiguration`, infers child type, and delegates to `WorkItem.CreateSeed()`.
3. **WorkItem.CreateSeed()** produces a `WorkItem` with `IsSeed=true`, `SeedCreatedAt=now`, and a unique negative sentinel ID via `Interlocked.Decrement`.
4. **SeedCommand pushes to ADO** — calls `adoService.CreateAsync(seed)` which POSTs to ADO, then `adoService.FetchAsync(newId)` to retrieve the full item with the positive ID assigned by ADO.
5. **Result is cached locally** — `workItemRepo.SaveAsync(created)` persists to SQLite with `is_seed=1`.

This means seeds are never truly local — they exist in ADO the moment they're created. The negative sentinel ID is transient, replaced immediately by the ADO-assigned positive ID.

### Relevant Components

| Component | File | Role |
|-----------|------|------|
| `SeedCommand` | `src/Twig/Commands/SeedCommand.cs` | Orchestrates seed creation, ADO push, caching |
| `SeedFactory` | `src/Twig.Domain/Services/SeedFactory.cs` | Static factory — validates types, creates seed |
| `WorkItem.CreateSeed()` | `src/Twig.Domain/Aggregates/WorkItem.cs` (line 143) | Factory method — assigns negative ID, sets `IsSeed` |
| `SqliteWorkItemRepository` | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Persistence — `GetSeedsAsync()`, `SaveAsync()` |
| `WorkingSetService` | `src/Twig.Domain/Services/WorkingSetService.cs` | Always fetches seeds via `GetSeedsAsync()` |
| `Workspace` | `src/Twig.Domain/ReadModels/Workspace.cs` | Read model — `Seeds`, `GetStaleSeeds()`, `ListAll()` |
| `SpectreRenderer` | `src/Twig/Rendering/SpectreRenderer.cs` | Renders seeds in workspace live view via `WorkspaceDataChunk.SeedsLoaded` |
| `HumanOutputFormatter` | `src/Twig/Formatters/HumanOutputFormatter.cs` | Renders seeds in human output (lines 305-319, 363-377) |
| `HintEngine` | `src/Twig/Hints/HintEngine.cs` | Post-seed hints (line 101), stale seed warnings on status (line 116) |
| `EditorLauncher` | `src/Twig/Commands/EditorLauncher.cs` | Editor integration — $VISUAL → $EDITOR → $GIT_EDITOR → git config; writes to `.twig/EDIT_MSG` |
| `EditCommand` | `src/Twig/Commands/EditCommand.cs` | Existing editor pattern — YAML-like `Key: Value` format, uses `IEditorLauncher`, `IPendingChangeStore` |
| `IFieldDefinitionStore` | `src/Twig.Domain/Interfaces/IFieldDefinitionStore.cs` | Field metadata from ADO — `GetAllAsync()`, `GetByReferenceNameAsync()` |
| `FieldDefinition` | `src/Twig.Domain/ValueObjects/FieldDefinition.cs` | Record: `ReferenceName`, `DisplayName`, `DataType`, `IsReadOnly` |
| `TwigConfiguration.SeedConfig` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | `StaleDays` (default 14), unused `DefaultChildType` dictionary |
| `TwigPaths` | `src/Twig.Infrastructure/Config/TwigPaths.cs` | `.twig/` directory path helpers |
| `IConsoleInput` | `src/Twig.Domain/Interfaces/IConsoleInput.cs` | User input abstraction — `ReadLine()`, `IsOutputRedirected` |
| `OutputFormatterFactory` | `src/Twig/Formatters/OutputFormatterFactory.cs` | Resolves `IOutputFormatter` by format name (human/json/minimal) |
| `RenderingPipelineFactory` | `src/Twig/Rendering/RenderingPipelineFactory.cs` | Resolves sync vs async rendering path based on format + TTY |
| `CommandRegistrationModule` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DI registration for all command classes — explicit factory lambdas |

### Command Routing Pattern

ConsoleAppFramework routes commands via public methods on `TwigCommands`:
- Method names become command names (e.g., `Seed()` → `twig seed`)
- `[Command("x y")]` attribute creates subcommands (e.g., `[Command("stash pop")]` → `twig stash pop`)
- `[Argument]` marks positional parameters; optional named parameters become `--flag` options
- Existing subcommand patterns: `stash`/`stash pop`, `hooks install`/`hooks uninstall`, `config`/`config status-fields`
- The framework resolves longest matching prefix first — `seed new` matches before `seed`

**Critical routing detail:** The bare `Seed()` method currently takes `[Argument] string title` as required. When adding `[Command("seed new")]`, the bare `Seed()` still handles `twig seed "title"` (one token after `seed`), while `[Command("seed new")]` handles `twig seed new ...` (explicit subcommand). This parallels the existing `Stash()` + `[Command("stash pop")]` pattern at lines 354-361 of Program.cs.

### Seed Protection During Refresh

Seeds with negative IDs survive `twig refresh` because:
1. `RefreshCommand` filters to `id > 0` when fetching from ADO — negative IDs are never sent to ADO.
2. `EvictExceptAsync` only evicts items not in the working set's keep set — seeds are always included via `WorkingSetService.ComputeAsync()` which calls `GetSeedsAsync()`.
3. `ProtectedCacheWriter` guards dirty items during batch saves.

This protection mechanism is **already correct** for the local-first model where seeds retain negative IDs indefinitely.

---

## Problem Statement

1. **Seeds are not drafts — they're immediately published.** The current design pushes seeds to ADO on creation, making them visible to the entire team before the creator has finished populating fields. There is no concept of a local draft.

2. **Seeds carry only a title.** The only field populated at creation time is the title. Description, effort, priority, and other fields require separate `twig edit` or `twig update` commands after the seed is already live in ADO.

3. **No seed management commands.** There is no way to view all seeds in a dashboard, edit a seed's fields, or discard a seed. The only entry point is `twig seed "title"`.

4. **No structured command tree.** The flat `twig seed "title"` command cannot accommodate the planned expansion (chain builder, validate, reconcile, publish in Plans 2–3) without a subcommand restructure.

---

## Goals and Non-Goals

### Goals

| # | Goal | Measure |
|---|------|---------|
| G1 | Seeds stay local until explicitly published | `SeedCommand` no longer calls `adoService.CreateAsync()` or `adoService.FetchAsync()` |
| G2 | Subcommand tree for seed management | `twig seed new`, `twig seed view`, `twig seed edit`, `twig seed discard` all functional |
| G3 | Backward compatibility for bare `twig seed "title"` | Alias routes to `twig seed new "title"` |
| G4 | Editor-driven field population | `twig seed new --editor` and `twig seed edit <id>` open structured editor buffer |
| G5 | Seed dashboard | `twig seed view` shows all seeds with type, title, parent, age, field completeness, stale warnings |
| G6 | Seed discard | `twig seed discard <id>` permanently deletes a local seed |
| G7 | Process-agnostic field editing | Editor fields driven by `IFieldDefinitionStore` metadata, not hardcoded |
| G8 | Domain model supports Plans 2–3 | No design decisions that preclude virtual links, publish rules, or validation |

### Non-Goals

| # | Non-Goal | Deferred To |
|---|----------|-------------|
| NG1 | Virtual links / `seed_links` table | Plan 2 |
| NG2 | Interactive chain builder (`twig seed chain`) | Plan 2 |
| NG3 | `twig next` / `twig prev` navigation | Plan 2 |
| NG4 | Publish rules / `.twig/seed-rules` | Plan 3 |
| NG5 | `twig seed validate` | Plan 3 |
| NG6 | `twig seed reconcile` | Plan 3 |
| NG7 | `twig seed publish` / ADO push | Plan 3 |
| NG8 | Backlog ordering on publish | Plan 3 |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `twig seed new "title"` creates a local seed with negative ID, persists to SQLite, does NOT call ADO |
| FR-2 | `twig seed "title"` (bare, no `new` subcommand) works as alias for `twig seed new "title"` |
| FR-3 | `twig seed new --editor` opens the user's editor with a structured field template; all writable fields from `field_definitions` are presented |
| FR-4 | `twig seed new --editor "title"` pre-fills title in the editor template |
| FR-5 | `twig seed view` displays a seed dashboard with: local ID, type badge, title, parent grouping, age, field completeness, stale warning |
| FR-6 | `twig seed edit <id>` opens a seed in the editor for field modification; saves updated fields back to SQLite |
| FR-7 | `twig seed discard <id>` deletes a seed from SQLite after confirmation (skippable with `--yes`) |
| FR-8 | Seed editor uses a section-header format (`# Field Name` / value) that is parseable and driven by `FieldDefinition` metadata |
| FR-9 | Both `SpectreRenderer` and `HumanOutputFormatter` rendering paths support the seed view dashboard |
| FR-10 | Seeds survive `twig refresh` without eviction or overwrite |
| FR-11 | Stale seed detection continues to work using `SeedCreatedAt` and `StaleDays` config |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Native AOT compatible — no reflection, source-gen JSON serialization only |
| NFR-2 | Process-agnostic — no assumptions about Agile/Scrum/CMMI/Basic process templates |
| NFR-3 | Subcommand routing extensible for Plans 2–3 (`chain`, `validate`, `reconcile`, `publish`) |
| NFR-4 | Existing test coverage for `SeedFactory` and `SeedCommand` updated, not broken |

---

## Proposed Design

### Architecture Overview

The redesign touches three layers:

```
┌──────────────────────────────────────────────────────────┐
│  CLI Layer (src/Twig/)                                    │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ SeedNewCmd    │  │ SeedViewCmd  │  │ SeedEditCmd   │  │
│  └──────┬───────┘  └──────┬───────┘  └───────┬───────┘  │
│         │                 │                   │          │
│  ┌──────┴───────┐  ┌──────────────┐  ┌───────┴───────┐  │
│  │ SeedDiscardCmd│  │ Renderers    │  │ EditorLauncher│  │
│  └──────────────┘  │(Spectre+Human│  │  (existing)   │  │
│                    └──────────────┘  └───────────────┘  │
├──────────────────────────────────────────────────────────┤
│  Domain Layer (src/Twig.Domain/)                          │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ SeedFactory   │  │WorkItem      │  │SeedEditorFmt  │  │
│  │ (unchanged)   │  │(+SetSeedField│  │ (NEW)         │  │
│  └──────────────┘  │  method)     │  │Generate()/    │  │
│                    └──────────────┘  │Parse()        │  │
│                                      └───────────────┘  │
├──────────────────────────────────────────────────────────┤
│  Infrastructure Layer (src/Twig.Infrastructure/)          │
│  ┌──────────────────────────────────────────────────────┐│
│  │ SqliteWorkItemRepository (+DeleteByIdAsync)          ││
│  └──────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. SeedCommand → Split into Subcommands

The existing `SeedCommand` is refactored into four focused command classes:

| Command | Class | Responsibility |
|---------|-------|----------------|
| `twig seed new "title"` | `SeedNewCommand` | Create local seed, optional editor workflow |
| `twig seed view` | `SeedViewCommand` | Render seed dashboard |
| `twig seed edit <id>` | `SeedEditCommand` | Edit seed fields in editor |
| `twig seed discard <id>` | `SeedDiscardCommand` | Delete local seed |

The original `SeedCommand.cs` file is deleted. Its `ExecuteAsync` logic migrates to `SeedNewCommand.ExecuteAsync` with the ADO push removed.

#### 2. SeedNewCommand (replaces SeedCommand)

**Constructor dependencies:**
- `ActiveItemResolver` — resolve parent context
- `IWorkItemRepository` — persist seed locally
- `IProcessConfigurationProvider` — validate type rules
- `IFieldDefinitionStore` — supply field metadata for editor
- `IEditorLauncher` — open editor when `--editor` is set
- `OutputFormatterFactory` — format output
- `HintEngine` — post-create hints

**Key change:** `IAdoWorkItemService` is **removed** from the dependency list. No ADO interaction. Compare with current `SeedCommand` constructor (line 14-20 of `SeedCommand.cs`) which takes `IAdoWorkItemService adoService`.

**DI registration change:** In `CommandRegistrationModule.cs` (line 93-99), the current factory creates `SeedCommand` with 6 dependencies including `IAdoWorkItemService`. The new factory for `SeedNewCommand` replaces this, adding `IFieldDefinitionStore` and `IEditorLauncher` while removing `IAdoWorkItemService`.

**ExecuteAsync flow:**
```
1. Validate title (unless --editor with no title → title comes from editor)
2. Resolve parent context via ActiveItemResolver
3. Validate type via SeedFactory.Create()
4. IF --editor:
   a. Generate editor buffer via SeedEditorFormat.Generate()
   b. Launch editor via IEditorLauncher.LaunchAsync()
   c. Parse result via SeedEditorFormat.Parse()
   d. Apply parsed fields to seed WorkItem
5. Persist seed via workItemRepo.SaveAsync(seed)
6. Output confirmation with local negative ID
7. Emit hints
```

#### 3. SeedEditorFormat (NEW domain service)

A stateless domain service in `src/Twig.Domain/Services/SeedEditorFormat.cs` responsible for generating and parsing the editor buffer format.

**Format specification:**
```
# Title
My work item title

# Description
Multi-line description
goes here.

# Effort
5

# Priority
2
```

**Rules:**
- Lines starting with `# ` (hash space) are section headers.
- The header text corresponds to the `DisplayName` of a `FieldDefinition`.
- All text between one header and the next (or EOF) is the field value, trimmed.
- Empty values are represented by blank lines below the header.
- Lines starting with `## ` (double hash) are comments — ignored during parse.
- The available sections are driven by `IFieldDefinitionStore.GetAllAsync()`, filtered to writable fields (`!IsReadOnly`).

**Public interface:**
```csharp
public static class SeedEditorFormat
{
    /// Generate an editor buffer for the given seed and field definitions.
    public static string Generate(
        WorkItem seed,
        IReadOnlyList<FieldDefinition> fieldDefinitions);

    /// Parse an edited buffer back into field key-value pairs.
    public static IReadOnlyDictionary<string, string?> Parse(
        string content,
        IReadOnlyList<FieldDefinition> fieldDefinitions);
}
```

**Field selection logic:**
1. Start with all `FieldDefinition` records from `GetAllAsync()`.
2. Filter to `!IsReadOnly`.
3. Exclude system-internal fields (e.g., `System.Id`, `System.Rev`, `System.CreatedDate`, `System.ChangedDate`, `System.Watermark`, `System.CreatedBy`, `System.ChangedBy`, `System.AuthorizedDate`, `System.RevisedDate`, `System.BoardColumn`, `System.BoardColumnDone`, `System.BoardLane`).
4. Present `System.Title` first, then `System.Description`, then remaining fields sorted by `DisplayName`.
5. Pre-populate values from `seed.Fields` dictionary where available.
6. For seeds created with no title argument in `--editor` mode, the `System.Title` value is left blank for the user to fill in.

This is fully process-agnostic — the fields presented depend entirely on what the ADO process template defines, as synced into `field_definitions` during `twig refresh`.

**Graceful degradation:** If `IFieldDefinitionStore.GetAllAsync()` returns an empty list (fresh workspace, no `twig refresh` yet), `SeedEditorFormat.Generate()` falls back to presenting only `Title` and `Description` sections (hardcoded as a minimum viable set). A `## ` comment instructs the user to run `twig refresh` to unlock all fields.

**Example generated buffer:**
```
## Seed editor — edit fields below. Lines starting with ## are ignored.
## Run 'twig refresh' to sync field definitions from ADO.

# Title
Implement OAuth callback

# Description
Handle the OAuth redirect and extract the authorization code.

# Effort
5

# Priority
2

# Acceptance Criteria

```

#### 4. SeedViewCommand (NEW)

**Constructor dependencies:**
- `IWorkItemRepository` — fetch seeds via `GetSeedsAsync()`
- `IFieldDefinitionStore` — count available writable fields for completeness indicator
- `TwigConfiguration` — `SeedConfig.StaleDays`
- `OutputFormatterFactory` — format output
- `RenderingPipelineFactory` — select Spectre vs sync path

**Dashboard layout:**

```
Seeds (4)
──────────────────────────────────────────────────────

  Parent: #123 User Story — Login flow
    -1  🔵 Task     Implement OAuth callback       3d ago   3/8 fields
    -2  🔵 Task     Add token refresh logic         1d ago   1/8 fields

  Parent: #456 Feature — Payment integration
    -3  🟢 User Story  Stripe webhook handler       14d ago  2/8 fields ⚠ stale

  Orphan Seeds
    -4  🟣 Epic     Q3 Planning                     0d ago   1/8 fields
```

**Grouping:** Seeds grouped by `ParentId`. Seeds with `ParentId == null` go under "Orphan Seeds". Parent metadata (title, type) fetched from `IWorkItemRepository.GetByIdAsync()`.

**Field completeness:** Count of non-empty fields in `seed.Fields` vs total writable fields from `IFieldDefinitionStore`. Displayed as `n/m fields`.

**Age calculation:** `DateTimeOffset.UtcNow - SeedCreatedAt`, formatted as `0d`, `3d`, `2w`, `1mo`.

#### 5. SeedEditCommand (NEW)

**Constructor dependencies:**
- `IWorkItemRepository` — load and save seed
- `IFieldDefinitionStore` — field metadata for editor
- `IEditorLauncher` — open editor
- `OutputFormatterFactory` — format output

**Flow:**
```
1. Parse <id> argument (must be negative — seed IDs are negative)
2. Load seed via workItemRepo.GetByIdAsync(id)
3. Validate seed exists and IsSeed == true
4. Load field definitions via fieldDefStore.GetAllAsync()
5. Generate editor buffer via SeedEditorFormat.Generate(seed, fieldDefs)
6. Launch editor via editorLauncher.LaunchAsync(buffer)
7. If null returned → cancelled, exit 0
8. Parse result via SeedEditorFormat.Parse(edited, fieldDefs)
9. Apply parsed fields to seed.Fields via seed.ImportFields() or SetField()
10. If title changed, create new WorkItem with updated Title (since Title is init-only)
11. Save via workItemRepo.SaveAsync(seed)
12. Output confirmation
```

**Challenge — WorkItem immutability:** `WorkItem.Title` is `init`-only (line 33 of `WorkItem.cs`). If the user changes the title in the editor, we need to create a new `WorkItem` with the updated title and all other properties copied over. We'll add a `WithSeedFields()` method to `WorkItem` that returns a new instance with updated title and fields while preserving all other properties (`Id`, `Type`, `IsSeed`, `SeedCreatedAt`, `ParentId`, `AreaPath`, `IterationPath`, `State`, `AssignedTo`, `Revision`). The old row is replaced in SQLite via `INSERT OR REPLACE` on the same negative ID.

#### 6. SeedDiscardCommand (NEW)

**Constructor dependencies:**
- `IWorkItemRepository` — load seed, delete seed
- `IConsoleInput` — confirmation prompt
- `OutputFormatterFactory` — format output

**Flow:**
```
1. Parse <id> argument
2. Load seed via workItemRepo.GetByIdAsync(id)
3. Validate seed exists and IsSeed == true
4. If not --yes: prompt "Discard seed #ID 'Title'? (y/N)"
5. Delete via workItemRepo.DeleteByIdAsync(id)
6. Output "Discarded seed #ID"
```

**New repository method:** `IWorkItemRepository` needs a `DeleteByIdAsync(int id)` method. The SQL is trivial: `DELETE FROM work_items WHERE id = @id`.

#### 7. WorkItem Changes

**New method: `WithSeedFields()`**
```csharp
public WorkItem WithSeedFields(
    string title,
    IReadOnlyDictionary<string, string?> fields)
```
Returns a new `WorkItem` instance with updated `Title` and fields, preserving `Id`, `Type`, `IsSeed`, `SeedCreatedAt`, `ParentId`, `AreaPath`, `IterationPath`, and all other properties. This is necessary because `Title` is `init`-only.

**New method: `SetSeedField()`**
```csharp
internal void SetSeedField(string fieldName, string? value)
```
Sets a field on a seed without marking it dirty (seeds are always "dirty" relative to ADO since they don't exist there yet). This wraps the existing `SetField()` internal method — it's the same operation but semantically distinct for clarity.

#### 8. Command Routing in Program.cs

```csharp
// Backward compat: bare "twig seed" routes to seed new
public async Task<int> Seed(
    [Argument] string title, string? type = null, bool editor = false,
    string output = ..., CancellationToken ct = default)
    => await services.GetRequiredService<SeedNewCommand>()
        .ExecuteAsync(title, type, editor, output, ct);

[Command("seed new")]
public async Task<int> SeedNew(
    [Argument] string? title = null, string? type = null, bool editor = false,
    string output = ..., CancellationToken ct = default)
    => await services.GetRequiredService<SeedNewCommand>()
        .ExecuteAsync(title, type, editor, output, ct);

[Command("seed view")]
public async Task<int> SeedView(
    string output = ..., CancellationToken ct = default)
    => await services.GetRequiredService<SeedViewCommand>()
        .ExecuteAsync(output, ct);

[Command("seed edit")]
public async Task<int> SeedEdit(
    [Argument] int id, string output = ..., CancellationToken ct = default)
    => await services.GetRequiredService<SeedEditCommand>()
        .ExecuteAsync(id, output, ct);

[Command("seed discard")]
public async Task<int> SeedDiscard(
    [Argument] int id, bool yes = false,
    string output = ..., CancellationToken ct = default)
    => await services.GetRequiredService<SeedDiscardCommand>()
        .ExecuteAsync(id, yes, output, ct);
```

**Note on backward compatibility:** The bare `Seed()` method (without `[Command]` attribute) handles `twig seed "title"`. The `[Command("seed new")]` handles `twig seed new "title"`. Both route to `SeedNewCommand`. The key difference: bare `Seed()` requires a title argument; `SeedNew()` makes title optional (can be provided via editor).

### Data Flow

#### Seed Creation (twig seed new "title")

```
User → TwigCommands.SeedNew() → SeedNewCommand.ExecuteAsync()
  → ActiveItemResolver.GetActiveItemAsync() → WorkItem? parent
  → SeedFactory.Create(title, parent, processConfig, typeOverride) → WorkItem seed
  → workItemRepo.SaveAsync(seed) → SQLite [is_seed=1, negative ID]
  → Console output: "Created local seed #-N: title (Type)"
  → HintEngine.GetHints("seed", createdId: seed.Id)
```

#### Seed Creation with Editor (twig seed new --editor)

```
User → SeedNewCommand.ExecuteAsync(title=null, editor=true)
  → ActiveItemResolver → parent
  → SeedFactory.Create(placeholderTitle, parent, config) → seed
  → fieldDefStore.GetAllAsync() → fieldDefs
  → SeedEditorFormat.Generate(seed, fieldDefs) → buffer string
  → editorLauncher.LaunchAsync(buffer) → edited string (or null=cancel)
  → SeedEditorFormat.Parse(edited, fieldDefs) → field dict
  → seed.WithSeedFields(title, fields) → updated seed
  → workItemRepo.SaveAsync(updatedSeed)
  → Console output
```

#### Seed View Dashboard (twig seed view)

```
User → SeedViewCommand.ExecuteAsync()
  → workItemRepo.GetSeedsAsync() → List<WorkItem> seeds
  → Group seeds by ParentId
  → For each group: workItemRepo.GetByIdAsync(parentId) → parent metadata
  → fieldDefStore.GetAllAsync() → count writable fields for completeness
  → Render via SpectreRenderer or HumanOutputFormatter
```

#### Seed Edit (twig seed edit -3)

```
User → SeedEditCommand.ExecuteAsync(id=-3)
  → workItemRepo.GetByIdAsync(-3) → seed
  → Validate seed.IsSeed == true
  → fieldDefStore.GetAllAsync() → fieldDefs
  → SeedEditorFormat.Generate(seed, fieldDefs) → buffer
  → editorLauncher.LaunchAsync(buffer) → edited
  → SeedEditorFormat.Parse(edited, fieldDefs) → fields
  → seed.WithSeedFields(newTitle, fields) → updatedSeed
  → workItemRepo.SaveAsync(updatedSeed)
  → Console output: "Updated seed #-3 (N field(s) changed)"
```

#### Seed Discard (twig seed discard -3)

```
User → SeedDiscardCommand.ExecuteAsync(id=-3, yes=false)
  → workItemRepo.GetByIdAsync(-3) → seed
  → Validate seed.IsSeed == true
  → Prompt: "Discard seed #-3 'Title'? (y/N)"
  → workItemRepo.DeleteByIdAsync(-3) → SQLite DELETE
  → Console output: "Discarded seed #-3"
```

### API Contracts

#### IWorkItemRepository — New Methods

```csharp
/// <summary>
/// Deletes a work item from the local cache by ID.
/// Used for discarding local-only seeds.
/// </summary>
Task DeleteByIdAsync(int id, CancellationToken ct = default);

/// <summary>
/// Returns the minimum (most negative) seed ID in the database,
/// or null if no seeds exist. Used to initialize the seed ID counter
/// to avoid collisions on process restart.
/// </summary>
Task<int?> GetMinSeedIdAsync(CancellationToken ct = default);
```

#### SeedEditorFormat — New Static Service

```csharp
public static class SeedEditorFormat
{
    public static string Generate(
        WorkItem seed,
        IReadOnlyList<FieldDefinition> fieldDefinitions);

    public static IReadOnlyDictionary<string, string?> Parse(
        string content,
        IReadOnlyList<FieldDefinition> fieldDefinitions);
}
```

#### WorkItem — New Methods

```csharp
/// <summary>
/// Returns a new WorkItem instance with updated title and fields,
/// preserving all other properties. Used for seed editing where Title
/// (init-only) may be changed by the user.
/// </summary>
public WorkItem WithSeedFields(
    string title,
    IReadOnlyDictionary<string, string?> fields);

/// <summary>
/// Initializes the static seed ID counter to avoid collisions with
/// existing seeds in the database. Should be called before the first
/// CreateSeed() call in a process, passing the MIN(id) from the DB.
/// Thread-safe via Interlocked.Exchange.
/// </summary>
public static void InitializeSeedCounter(int minExistingId);
```

#### IOutputFormatter — New Method

```csharp
string FormatSeedView(
    IReadOnlyList<SeedViewGroup> groups,
    int totalWritableFields,
    int staleDays);
```

Where `SeedViewGroup` is a new read model:

```csharp
public sealed record SeedViewGroup(
    WorkItem? Parent,
    IReadOnlyList<WorkItem> Seeds);
```

### Design Decisions

#### D1: Static SeedEditorFormat vs Instance Service

**Decision:** Static class (like `SeedFactory`).

**Rationale:** `SeedEditorFormat` is a pure function — it takes inputs and produces outputs with no state, I/O, or dependencies. The field definitions are passed as parameters. This follows the existing `SeedFactory` pattern and avoids unnecessary DI registration.

#### D2: Section-Header Format vs YAML/JSON

**Decision:** Section-header format (`# Field Name` / value).

**Rationale:** 
- YAML requires a parser library, which may not be AOT-friendly and adds a dependency.
- JSON is not human-friendly for multi-line fields like Description.
- The section-header format is trivially parseable with string operations, supports multi-line values naturally, and is familiar to users of `git commit` and similar tools.
- `## ` prefix for comments mirrors git commit message conventions.

#### D3: `WithSeedFields()` Copy Method vs Mutable Title

**Decision:** Add a `WithSeedFields()` method that returns a new `WorkItem`.

**Rationale:** `WorkItem.Title` is `init`-only by design to enforce immutability of identity properties. Rather than weakening this invariant, we create a copy method that produces a new `WorkItem` with the updated title and fields. This is consistent with the record-like immutability pattern used elsewhere in the domain.

#### D4: Bare `twig seed "title"` as Alias vs Deprecation

**Decision:** Keep as permanent alias, not deprecated.

**Rationale:** `twig seed "title"` is the most common use case — quick capture of an idea. Adding friction by requiring `twig seed new` would harm the ergonomic advantage. The alias is zero-cost to maintain.

#### D5: Field Filtering for Editor

**Decision:** Filter field definitions to writable fields, excluding system-internal fields, presented in a standard order.

**Rationale:** Showing all 100+ ADO fields would overwhelm the editor. The filter:
1. Excludes `IsReadOnly` fields (can't be set).
2. Excludes known system fields that are auto-managed: `System.Id`, `System.Rev`, `System.CreatedDate`, `System.ChangedDate`, `System.Watermark`, `System.CreatedBy`, `System.ChangedBy`, `System.AuthorizedDate`, `System.RevisedDate`, `System.BoardColumn`, `System.BoardColumnDone`, `System.BoardLane`.
3. Shows `System.Title` and `System.Description` first (most commonly edited).
4. Remaining fields sorted alphabetically by `DisplayName`.

This is process-agnostic — CMMI's "Requirement" fields will appear just like Agile's "User Story" fields, driven entirely by the `field_definitions` table content.

#### D6: SeedViewGroup Read Model

**Decision:** Introduce a `SeedViewGroup` record to carry grouped seed data to renderers.

**Rationale:** The renderers need seeds grouped by parent, with parent metadata. Rather than passing raw data and having each renderer re-group, we compute the grouping once in `SeedViewCommand` and pass the structured result to the formatter/renderer.

#### D7: Seed ID Counter Safety

**Decision:** `SeedNewCommand` queries `MIN(id) FROM work_items WHERE is_seed = 1` before the first `CreateSeed()` call and initializes the static counter via a new `WorkItem.InitializeSeedCounter(int minExistingId)` method.

**Rationale:** The current `WorkItem._seedIdCounter` (a static `int` initialized to 0) resets on every process restart. If seeds -1 and -2 already exist in SQLite, a new `twig seed new` would produce -1 again, silently overwriting via `INSERT OR REPLACE`. The fix:

1. Add `public static void InitializeSeedCounter(int minExistingId)` to `WorkItem`. Sets `_seedIdCounter` to `Math.Min(minExistingId, 0)` so the next `Interlocked.Decrement` produces `minExistingId - 1`.
2. Add `Task<int?> GetMinSeedIdAsync(CancellationToken ct)` to `IWorkItemRepository` — returns `MIN(id) FROM work_items WHERE is_seed = 1`.
3. `SeedNewCommand.ExecuteAsync()` calls `GetMinSeedIdAsync()` and passes the result to `InitializeSeedCounter()` before creating the seed.
4. Thread-safety: `InitializeSeedCounter` uses `Interlocked.Exchange` to set `_seedIdCounter` atomically. In the CLI's single-threaded usage pattern, this is belt-and-suspenders.

This is a minimal, non-invasive fix that doesn't require startup-time plumbing or DI changes.

---

## Alternatives Considered

### Alt-1: Keep Single SeedCommand, Add Flags

Instead of splitting into subcommand classes, add `--view`, `--edit <id>`, `--discard <id>` flags to the existing `SeedCommand`.

**Pros:** Fewer files, simpler routing.
**Cons:** Violates single-responsibility, makes future extensions (chain, validate, publish) harder, inconsistent with existing subcommand patterns (`hooks install`, `stash pop`).

**Decision:** Rejected. Subcommand tree is more maintainable and consistent with the codebase's existing patterns.

### Alt-2: YAML Editor Format

Use YAML for the editor buffer format.

**Pros:** Widely known, handles nested structures.
**Cons:** Requires a YAML parser (AOT compatibility concern), poor multi-line UX, overkill for flat key-value fields.

**Decision:** Rejected. Section-header format is simpler and more appropriate.

### Alt-3: Keep ADO Push, Add --local Flag

Instead of removing the ADO push by default, add a `--local` flag to keep seeds local.

**Pros:** No breaking change.
**Cons:** Defeats the purpose — the goal is local-first by default. Users who want immediate push can use `twig seed new` + `twig seed publish` (Plan 3).

**Decision:** Rejected. Local-first is the core behavioral change.

---

## Dependencies

### External Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| ConsoleAppFramework | Existing | Subcommand routing via `[Command]` attribute |
| Spectre.Console | Existing | Dashboard rendering for `seed view` |
| SQLite (via raw ADO.NET) | Existing | Persistence layer |

### Internal Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| `IFieldDefinitionStore` | Existing | Must be populated (requires `twig refresh` or `twig init` to have run). `SqliteFieldDefinitionStore.GetAllAsync()` returns `FieldDefinition` records with `ReferenceName`, `DisplayName`, `DataType`, `IsReadOnly`. |
| `IEditorLauncher` / `EditorLauncher` | Existing | Reused as-is for `new --editor` and `edit`. Writes to `.twig/EDIT_MSG`, returns null on abort/unchanged. Already registered in `CommandServiceModule.cs` line 29. |
| `ProcessConfiguration` | Existing | Reused by `SeedFactory` — unchanged |
| `IWorkItemRepository` | Existing, modified | `DeleteByIdAsync` and `GetMinSeedIdAsync` added |
| `IConsoleInput` | Existing | Reused for `seed discard` confirmation prompt. Already registered in `CommandServiceModule.cs` line 30. |
| `WorkItemBuilder` (test kit) | Existing | `.AsSeed(daysOld)` method already supports seed test fixtures (`tests/Twig.TestKit/WorkItemBuilder.cs` line 47) |

### Sequencing Constraints

- `IFieldDefinitionStore` must contain data for the editor to be useful. If `field_definitions` is empty (fresh workspace, no refresh yet), the editor should gracefully degrade to showing only `Title` and `Description`.
- Plan 2 (virtual links) depends on the subcommand routing from this plan being in place.
- Plan 3 (publish) depends on the local-first seed model from this plan.

---

## Impact Analysis

### Components Affected

| Component | Impact | Details |
|-----------|--------|---------|
| `SeedCommand` | **Deleted** | Replaced by `SeedNewCommand` |
| `Program.cs` TwigCommands | **Modified** | Seed routing restructured to subcommands |
| `CommandRegistrationModule` | **Modified** | New command registrations, old removed |
| `IWorkItemRepository` | **Modified** | `DeleteByIdAsync` added |
| `SqliteWorkItemRepository` | **Modified** | `DeleteByIdAsync` implemented |
| `WorkItem` | **Modified** | `WithSeedFields()` added |
| `HintEngine` | **Modified** | Seed hints updated for local-first (no ADO ID reference) |
| `IOutputFormatter` | **Modified** | `FormatSeedView()` added |
| `HumanOutputFormatter` | **Modified** | `FormatSeedView()` implemented |
| `JsonOutputFormatter` | **Modified** | `FormatSeedView()` implemented |
| `MinimalOutputFormatter` | **Modified** | `FormatSeedView()` implemented |
| `SpectreRenderer` | **Modified** | Seed dashboard rendering added |
| `IAsyncRenderer` | **Modified** | `RenderSeedViewAsync()` added |
| `GroupedHelp` (in `Program.cs`) | **Modified** | Update seed entry from `seed <title>` to reflect subcommand tree |
| Existing tests | **Modified** | `SeedCommandTests` rewritten for local-first behavior |

### Backward Compatibility

| Area | Impact | Mitigation |
|------|--------|------------|
| `twig seed "title"` CLI usage | **Breaking behavior** — no longer pushes to ADO | Bare command still works, but seed stays local |
| Scripts depending on ADO ID in output | **Breaking** — output shows negative local ID, not ADO ID | Migration guidance in changelog |
| `twig status` seed display | **No change** — seeds still appear in workspace views | N/A |
| `twig refresh` | **No change** — already skips negative IDs | N/A |

### Performance Implications

- **Positive:** Seed creation is now instant (no network call to ADO). Creates go from ~500ms–2s to <10ms.
- **Neutral:** Seed view dashboard adds a new query pattern but uses existing indexed `is_seed=1` partial index.
- **Neutral:** `DeleteByIdAsync` is a single-row DELETE by primary key — trivially fast.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Users depend on seeds appearing in ADO immediately | Medium | High | Document breaking change prominently. Stretch goal: `--publish` flag on `seed new` for transition period (not required for Plan 1). |
| `field_definitions` empty on fresh workspace | Medium | Low | Graceful degradation: editor shows only Title + Description when no field metadata available. Show hint: "Run `twig refresh` to sync field definitions." |
| ConsoleAppFramework routing conflict between `Seed()` and `[Command("seed new")]` | Low | High | Verified: existing pattern (`Stash()` + `[Command("stash pop")]`) works. ConsoleAppFramework matches longest prefix first. |
| WorkItem `_seedIdCounter` reset on process restart causes ID collisions | Low | Medium | Resolved in design (D7): `SeedNewCommand` queries `MIN(id)` from DB and initializes counter via `WorkItem.InitializeSeedCounter()`. |
| Editor format parsing ambiguity with `#` in field values | Low | Low | Only `# ` (hash + space) at line start is treated as a section header. `## ` is a comment. `#` elsewhere in a line is literal content. Document in editor buffer header comment. |
| `EditorLauncher` timeout kills editor after 5 minutes | Low | Low | Existing behavior — user may need more time for large editor sessions. Not changed by this plan; can be addressed separately if needed. |

---

## Open Questions

1. **[Low]** Should `twig seed view` support filtering by parent ID (`twig seed view --parent 123`)? This would be useful for large seed counts but adds complexity. Can be added later without breaking changes.

2. **[Low]** Should the editor format include a comment block at the top explaining the format? (e.g., `## Lines starting with ## are comments. Each # Section is a field.`) Recommended: yes, for discoverability. The proposed design includes this.

3. **[Low]** Should `twig seed discard` support discarding multiple seeds at once (`twig seed discard -1 -2 -3`)? Single-ID is simpler for Plan 1; batch discard can be added later.

4. **[Low]** What should `twig seed edit <id>` do if the seed has already been published (positive ID, `is_seed=1`)? For Plan 1, published seeds don't exist (no publish command), so this is moot. Plan 3 should address this.

5. **[Low]** Should the `WorkItem._seedIdCounter` initialization be done eagerly at startup or lazily on first seed creation? The proposed design initializes it lazily via a static `InitializeCounter()` method called in `SeedNewCommand` before the first `CreateSeed()` call. Eager initialization would require plumbing through the DI container.

**NOTE:** The seed ID counter collision issue (previously Moderate severity) has been resolved in the design. See Design Decision D7 below for the solution: `SeedNewCommand` queries `MIN(id) FROM work_items WHERE is_seed = 1` and initializes the static counter via a new `WorkItem.InitializeSeedCounter(int minExistingId)` method before calling `CreateSeed()`.

---

## Implementation Phases

### Phase 1: Domain Foundation
**Exit Criteria:** `SeedEditorFormat`, `WorkItem.WithSeedFields()`, `IWorkItemRepository.DeleteByIdAsync`, and seed ID counter initialization are implemented and tested.

### Phase 2: Core Commands
**Exit Criteria:** `SeedNewCommand` (local-only, no ADO), `SeedEditCommand`, `SeedDiscardCommand` are implemented. Backward-compatible routing in `Program.cs`. All existing tests updated.

### Phase 3: Seed View Dashboard
**Exit Criteria:** `SeedViewCommand` with both Spectre and Human rendering paths. Seed dashboard shows grouping, age, field completeness, stale warnings.

### Phase 4: Integration & Polish
**Exit Criteria:** All commands registered in DI. Hint engine updated. End-to-end manual testing. All tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/SeedEditorFormat.cs` | Generate/Parse editor buffer for seed field editing |
| `src/Twig/Commands/SeedNewCommand.cs` | Local-first seed creation with optional editor workflow |
| `src/Twig/Commands/SeedViewCommand.cs` | Seed dashboard command |
| `src/Twig/Commands/SeedEditCommand.cs` | Edit seed fields in editor |
| `src/Twig/Commands/SeedDiscardCommand.cs` | Discard (delete) local seeds |
| `src/Twig.Domain/ReadModels/SeedViewGroup.cs` | Read model for grouped seed dashboard data |
| `tests/Twig.Domain.Tests/Services/SeedEditorFormatTests.cs` | Unit tests for editor format Generate/Parse |
| `tests/Twig.Cli.Tests/Commands/SeedNewCommandTests.cs` | Tests for local-first seed creation |
| `tests/Twig.Cli.Tests/Commands/SeedViewCommandTests.cs` | Tests for seed dashboard |
| `tests/Twig.Cli.Tests/Commands/SeedEditCommandTests.cs` | Tests for seed editing |
| `tests/Twig.Cli.Tests/Commands/SeedDiscardCommandTests.cs` | Tests for seed discard |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Add `WithSeedFields()` method, add `InitializeSeedCounter()` static method |
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `DeleteByIdAsync()`, add `GetMinSeedIdAsync()` |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement `DeleteByIdAsync()`, implement `GetMinSeedIdAsync()` |
| `src/Twig/Program.cs` | Restructure seed routing to subcommands |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register new command classes, remove old `SeedCommand` |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatSeedView()` |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatSeedView()` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatSeedView()` |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatSeedView()` |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Add `RenderSeedViewAsync()` |
| `src/Twig/Rendering/SpectreRenderer.cs` | Implement `RenderSeedViewAsync()` |
| `src/Twig/Hints/HintEngine.cs` | Update seed hints for local-first model; update stale seed hint to suggest `twig seed view` |
| `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | No changes needed (factory logic unchanged) — verified: 12 existing tests all remain valid |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/SeedCommand.cs` | Replaced by `SeedNewCommand.cs` |

---

## Implementation Plan

### Epic 1: Domain Foundation

**Goal:** Establish the domain-layer building blocks — editor format service, WorkItem copy method, repository delete, and seed ID counter safety.

**Prerequisites:** None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Add `WithSeedFields(string title, IReadOnlyDictionary<string, string?> fields)` method to `WorkItem` that returns a new instance with updated title and fields, preserving Id, Type, IsSeed, SeedCreatedAt, ParentId, AreaPath, IterationPath, State, AssignedTo, Revision | `WorkItem.cs` | DONE |
| E1-T2 | TEST | Unit tests for `WithSeedFields()` — verify all properties copied, title/fields updated, dirty flag behavior | `WorkItemTests.cs` (new or existing in `tests/Twig.Domain.Tests/Aggregates/`) | DONE |
| E1-T3 | IMPL | Add `InitializeSeedCounter(int minExistingId)` static method to `WorkItem` using `Interlocked.Exchange` to set `_seedIdCounter` to `Math.Min(minExistingId, 0)` | `WorkItem.cs` | DONE |
| E1-T4 | IMPL | Create `SeedEditorFormat.Generate()` — produces editor buffer from seed + field definitions. Include `## ` comment header explaining format. Filter to writable fields, exclude system-internal fields, order Title → Description → alphabetical | `SeedEditorFormat.cs` | DONE |
| E1-T5 | IMPL | Create `SeedEditorFormat.Parse()` — parses edited buffer back into field dictionary. Handle multi-line values, ignore `## ` comments, map display names back to reference names via `FieldDefinition` | `SeedEditorFormat.cs` | DONE |
| E1-T6 | TEST | Unit tests for `SeedEditorFormat` — generate roundtrip, parse multi-line values, handle comments, missing fields, empty input, graceful degradation with empty field definitions | `SeedEditorFormatTests.cs` | DONE |
| E1-T7 | IMPL | Add `DeleteByIdAsync(int id)` to `IWorkItemRepository` interface | `IWorkItemRepository.cs` | DONE |
| E1-T8 | IMPL | Add `GetMinSeedIdAsync()` to `IWorkItemRepository` interface | `IWorkItemRepository.cs` | DONE |
| E1-T9 | IMPL | Implement `DeleteByIdAsync` in `SqliteWorkItemRepository` — `DELETE FROM work_items WHERE id = @id` | `SqliteWorkItemRepository.cs` | DONE |
| E1-T10 | IMPL | Implement `GetMinSeedIdAsync` in `SqliteWorkItemRepository` — `SELECT MIN(id) FROM work_items WHERE is_seed = 1` | `SqliteWorkItemRepository.cs` | DONE |
| E1-T11 | TEST | Integration tests for `DeleteByIdAsync` and `GetMinSeedIdAsync` | `SqliteWorkItemRepositoryTests.cs` (existing or new in `tests/Twig.Infrastructure.Tests/`) | DONE |
| E1-T12 | TEST | Test that `InitializeSeedCounter` + `CreateSeed` avoids ID collisions with existing seeds | New test in `tests/Twig.Domain.Tests/Aggregates/` | DONE |
| E1-T13 | IMPL | Create `SeedViewGroup` read model record in `src/Twig.Domain/ReadModels/` | `SeedViewGroup.cs` | DONE |

**Acceptance Criteria:**
- [x] `SeedEditorFormat.Generate()` produces correct section-header format for any process template
- [x] `SeedEditorFormat.Parse()` correctly roundtrips — `Parse(Generate(seed, defs), defs)` yields original field values
- [x] `WorkItem.WithSeedFields()` preserves all properties except title and fields
- [x] `DeleteByIdAsync` removes a single row by ID
- [x] Seed ID counter initialized from DB avoids collisions with existing seeds
- [x] All new tests pass

---

### Epic 2: SeedNewCommand (Local-First Creation)

**Goal:** Replace `SeedCommand` with `SeedNewCommand` that creates seeds locally without ADO interaction, supports editor workflow.

**Prerequisites:** Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Create `SeedNewCommand` with primary constructor taking `ActiveItemResolver`, `IWorkItemRepository`, `IProcessConfigurationProvider`, `IFieldDefinitionStore`, `IEditorLauncher`, `OutputFormatterFactory`, `HintEngine`. Implement `ExecuteAsync(string? title, string? type, bool editor, string outputFormat, CancellationToken)` with local-only creation (no ADO) | `SeedNewCommand.cs` | DONE |
| E2-T2 | IMPL | Add `--editor` workflow to `SeedNewCommand` — call `GetMinSeedIdAsync` + `InitializeSeedCounter`, call `SeedFactory.Create`, then `fieldDefStore.GetAllAsync()`, `SeedEditorFormat.Generate()`, `editorLauncher.LaunchAsync()`, `SeedEditorFormat.Parse()`, apply fields via `WithSeedFields()`, save | `SeedNewCommand.cs` | DONE |
| E2-T3 | IMPL | Handle `--editor` with and without title: if title is null, use placeholder `"(untitled)"` for SeedFactory, then let editor provide real title. If title is provided, pre-fill in editor buffer | `SeedNewCommand.cs` | DONE |
| E2-T4 | IMPL | Wire routing in `Program.cs` — keep bare `Seed([Argument] string title, ...)` routing to `SeedNewCommand`. Add `[Command("seed new")] SeedNew([Argument] string? title = null, ..., bool editor = false)` routing to `SeedNewCommand` | `Program.cs` | DONE |
| E2-T5 | IMPL | Update `GroupedHelp.Show()` in `Program.cs` — change seed entry from `seed <title>` to show `seed new <title>` and subcommands | `Program.cs` | DONE |
| E2-T6 | IMPL | Register `SeedNewCommand` in `CommandRegistrationModule` with factory lambda (7 deps). Remove `SeedCommand` registration (lines 93-99) | `CommandRegistrationModule.cs` | DONE |
| E2-T7 | IMPL | Delete `SeedCommand.cs` | `SeedCommand.cs` | DONE |
| E2-T8 | IMPL | Update `HintEngine` seed hints — change `$"Created #{createdId.Value}. Try: twig set {createdId.Value} to switch context"` to reference local ID and suggest `twig seed edit` and `twig seed view` | `HintEngine.cs` | DONE |
| E2-T9 | TEST | Write `SeedNewCommandTests` — verify: no `IAdoWorkItemService` dependency, local creation produces negative ID, `SaveAsync` called, editor flow with mock `IEditorLauncher`, backward compat via bare `Seed()` | `SeedNewCommandTests.cs` | DONE |
| E2-T10 | TEST | Verify no existing test file references `SeedCommand` (there isn't one — `tests/Twig.Cli.Tests/Commands/` has no `SeedCommandTests.cs`). If any do, update them | Existing test files | DONE |

**Acceptance Criteria:**
- [x] `twig seed "title"` creates a local seed with negative ID — no ADO calls
- [x] `twig seed new "title"` creates a local seed with negative ID
- [x] `twig seed new --editor` opens editor with field template
- [x] `twig seed new --editor "title"` opens editor with title pre-filled
- [x] `IAdoWorkItemService` is NOT a dependency of `SeedNewCommand`
- [x] Seed counter initialized from DB before creation — no ID collisions
- [x] Existing `SeedCommand.cs` is deleted
- [x] `GroupedHelp` updated with new subcommand tree
- [x] All tests pass (no `SeedCommandTests` to worry about — none existed)

**Completion Notes:** All 2,575 tests pass. 15 new tests in `SeedNewCommandTests` cover all scenarios. Note: `HintEngine` seed hints reference `twig seed edit` and `twig seed view` which do not yet exist — forward-looking hints intentionally point to commands implemented in Epic 3/4.

---

### Epic 3: SeedEditCommand and SeedDiscardCommand

**Goal:** Implement seed editing and discarding capabilities.

**Prerequisites:** Epic 1, Epic 2

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Create `SeedEditCommand` with primary constructor taking `IWorkItemRepository`, `IFieldDefinitionStore`, `IEditorLauncher`, `OutputFormatterFactory`. Implement `ExecuteAsync(int id, string outputFormat, CancellationToken)` — load seed, validate `IsSeed`, generate editor buffer, launch editor, parse changes, compute diff, apply via `WithSeedFields()`, save via `SaveAsync()` | `SeedEditCommand.cs` | TO DO |
| E3-T2 | IMPL | Create `SeedDiscardCommand` with primary constructor taking `IWorkItemRepository`, `IConsoleInput`, `OutputFormatterFactory`. Implement `ExecuteAsync(int id, bool yes, string outputFormat, CancellationToken)` — load seed, validate `IsSeed`, prompt confirmation (skip if `--yes`), delete via `DeleteByIdAsync()` | `SeedDiscardCommand.cs` | TO DO |
| E3-T3 | IMPL | Wire `[Command("seed edit")] SeedEdit([Argument] int id, ...)` and `[Command("seed discard")] SeedDiscard([Argument] int id, bool yes = false, ...)` routing in `Program.cs` TwigCommands class | `Program.cs` | TO DO |
| E3-T4 | IMPL | Register `SeedEditCommand` and `SeedDiscardCommand` in `CommandRegistrationModule.AddCoreCommands()` with factory lambdas | `CommandRegistrationModule.cs` | TO DO |
| E3-T5 | TEST | Write `SeedEditCommandTests` — load/edit/save flow with mock `IEditorLauncher`, cancel handling (null return), non-seed ID rejection, title change creates new WorkItem | `SeedEditCommandTests.cs` | TO DO |
| E3-T6 | TEST | Write `SeedDiscardCommandTests` — successful discard with confirmation, --yes flag bypass, non-seed ID rejection, non-existent ID error, prompt rejection cancels | `SeedDiscardCommandTests.cs` | TO DO |

**Acceptance Criteria:**
- [ ] `twig seed edit <id>` opens editor with current seed fields, saves changes
- [ ] `twig seed edit <id>` on non-seed ID returns error
- [ ] `twig seed discard <id>` prompts and deletes seed
- [ ] `twig seed discard <id> --yes` skips prompt
- [ ] `twig seed discard <id>` on non-seed ID returns error
- [ ] All tests pass

---

### Epic 4: Seed View Dashboard

**Goal:** Implement the seed dashboard with both rendering paths.

**Prerequisites:** Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Create `SeedViewCommand` with primary constructor taking `IWorkItemRepository`, `IFieldDefinitionStore`, `TwigConfiguration`, `OutputFormatterFactory`, `RenderingPipelineFactory`. Implement `ExecuteAsync(string outputFormat, CancellationToken)` — query seeds via `GetSeedsAsync()`, group by `ParentId`, fetch parent metadata, count writable fields for completeness, compute age, route to renderer | `SeedViewCommand.cs` | TO DO |
| E4-T2 | IMPL | Add `FormatSeedView(IReadOnlyList<SeedViewGroup> groups, int totalWritableFields, int staleDays)` to `IOutputFormatter` interface | `IOutputFormatter.cs` | TO DO |
| E4-T3 | IMPL | Implement `FormatSeedView()` in `HumanOutputFormatter` — grouped display with ANSI colors, type badges (reuse `GetTypeBadge`/`GetTypeColor`), age formatting, field completeness `n/m fields`, stale warning `⚠ stale` | `HumanOutputFormatter.cs` | TO DO |
| E4-T4 | IMPL | Implement `FormatSeedView()` in `JsonOutputFormatter` — structured JSON array with groups | `JsonOutputFormatter.cs` | TO DO |
| E4-T5 | IMPL | Implement `FormatSeedView()` in `MinimalOutputFormatter` — compact one-line-per-seed format | `MinimalOutputFormatter.cs` | TO DO |
| E4-T6 | IMPL | Add `RenderSeedViewAsync(Func<Task<IReadOnlyList<SeedViewGroup>>> getData, int totalWritableFields, int staleDays, CancellationToken ct)` to `IAsyncRenderer` and implement in `SpectreRenderer` with Spectre.Console table rendering | `IAsyncRenderer.cs`, `SpectreRenderer.cs` | TO DO |
| E4-T7 | IMPL | Wire `[Command("seed view")] SeedView(string output = ..., CancellationToken ct = default)` routing in `Program.cs` | `Program.cs` | TO DO |
| E4-T8 | IMPL | Register `SeedViewCommand` in `CommandRegistrationModule.AddCoreCommands()` with factory lambda | `CommandRegistrationModule.cs` | TO DO |
| E4-T9 | TEST | Write `SeedViewCommandTests` — empty seeds shows "No seeds", multiple parents grouped correctly, orphan seeds grouped under "Orphan Seeds", stale detection based on StaleDays, field completeness calculation | `SeedViewCommandTests.cs` | TO DO |

**Acceptance Criteria:**
- [ ] `twig seed view` shows seeds grouped by parent
- [ ] Each seed shows: ID, type badge, title, age, field completeness, stale warning
- [ ] Orphan seeds (no parent) grouped under "Orphan Seeds" header
- [ ] Human, JSON, and Minimal output formats all produce correct output
- [ ] Spectre live rendering path works for `seed view`
- [ ] Empty seed list shows "No seeds" message
- [ ] All tests pass

---

### Epic 5: Integration & Polish

**Goal:** Final integration, DI wiring validation, hint updates, and end-to-end verification.

**Prerequisites:** Epics 2, 3, 4

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | Verify all DI registrations compile and resolve correctly — build `Twig.csproj` with no errors | `CommandRegistrationModule.cs` | TO DO |
| E5-T2 | IMPL | Verify `twig refresh` still skips seeds (negative IDs) — read `RefreshCommand.cs` line 92 (`var realIds = ids.Where(id => id > 0).ToList()`), confirm no regression path | `RefreshCommand.cs` (read-only verify) | TO DO |
| E5-T3 | IMPL | Verify `twig workspace` / `twig status` still show seeds correctly — existing `WorkspaceDataChunk.SeedsLoaded` rendering in `SpectreRenderer.cs` (lines 120-159) and `HumanOutputFormatter.cs` (lines 305-377) still works | SpectreRenderer, HumanOutputFormatter (read-only verify) | TO DO |
| E5-T4 | TEST | Build entire solution (`dotnet build Twig.slnx`), run all existing tests (`dotnet test`), fix any regressions | All test projects | TO DO |
| E5-T5 | IMPL | Update stale seed hint in `HintEngine` for status command (line 119) to suggest `twig seed view` instead of generic message | `HintEngine.cs` | TO DO |
| E5-T6 | IMPL | Verify `EvictExceptAsync` in `SqliteWorkItemRepository` does not evict seeds — seeds are in `WorkingSet.SeedIds` which feeds into `AllIds` keep set. Trace through `SyncCoordinator.SyncWorkingSetAsync()` to confirm. | `SyncCoordinator.cs`, `WorkingSet.cs` (read-only verify) | TO DO |

**Acceptance Criteria:**
- [ ] `dotnet build Twig.slnx` succeeds with no warnings in modified projects
- [ ] All existing tests pass (no regressions) — `dotnet test`
- [ ] All new tests pass
- [ ] `twig seed "title"` backward compat verified (routes to `SeedNewCommand`)
- [ ] `twig seed new`, `twig seed view`, `twig seed edit`, `twig seed discard` all functional
- [ ] Seeds survive `twig refresh` — negative IDs not fetched from ADO, not evicted
- [ ] Stale seed warnings appear correctly (based on `SeedCreatedAt` and `StaleDays`)
- [ ] `ProtectedCacheWriter` does not overwrite dirty seeds during refresh

---

## References

- [ConsoleAppFramework GitHub](https://github.com/Cysharp/ConsoleAppFramework) — Command routing and subcommand patterns
- [Azure DevOps REST API — Work Items](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items) — Work item field reference
- [Azure DevOps REST API — Fields](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/fields/list) — Field definitions endpoint
- Existing codebase patterns: `stash`/`stash pop`, `hooks install`/`hooks uninstall`, `config`/`config status-fields` for subcommand precedent
- `EditCommand.cs` — Editor integration pattern to reuse
- `SeedFactory.cs` — Static factory pattern to follow for `SeedEditorFormat`
