# Twig CLI — Structural Audit Report

> **Generated**: 2025-03-15
> **Scope**: All `.cs` files in `src/Twig.Domain/`, `src/Twig/`, `src/Twig.Infrastructure/` (obj/bin excluded)
> **Purpose**: Identify redundancies, layer violations, code smells, and domain promotion candidates

---

## 1. Type Inventory

### Domain Layer (`src/Twig.Domain/`)

| Subdirectory | Type | Kind | ~Lines | Description |
|---|---|---|---|---|
| Aggregates/ | `WorkItem` | class (sealed) | 130 | Root aggregate with command-queue pattern for mutations |
| Aggregates/ | `ProcessTypeRecord` | class (sealed) | 25 | Process type metadata from ADO (persisted to SQLite) |
| Aggregates/ | `ProcessConfiguration` | class (sealed) | 270 | Immutable aggregate encoding ADO process template rules |
| Aggregates/ | `TypeConfig` | class (sealed) | 25 | Per-type config (states, child types, transition rules) |
| Commands/ | `IWorkItemCommand` | interface | 25 | Command pattern interface for WorkItem mutations |
| Commands/ | `AddNoteCommand` | class (sealed) | 25 | Appends pending note to work item |
| Commands/ | `ChangeStateCommand` | class (sealed) | 40 | Transitions work item state |
| Commands/ | `UpdateFieldCommand` | class (sealed) | 35 | Sets arbitrary field value |
| Common/ | `PendingChangeRecord` | record (sealed) | 10 | Recorded pending change DTO |
| Common/ | `Result` | record struct | 25 | Non-generic operation outcome |
| Common/ | `Result<T>` | record struct | 30 | Generic operation outcome with value |
| Enums/ | `ProcessTemplate` | enum | 10 | Agile, Scrum, CMMI, Basic |
| Enums/ | `TransitionKind` | enum | 20 | None, Forward, Backward, Cut |
| Interfaces/ | `IAdoWorkItemService` | interface | 20 | ADO REST contract for work items |
| Interfaces/ | `IAuthenticationProvider` | interface | 10 | Token provider contract |
| Interfaces/ | `IConsoleInput` | interface | 12 | Testable console input abstraction |
| Interfaces/ | `IContextStore` | interface | 14 | Active context and key-value settings |
| Interfaces/ | `IEditorLauncher` | interface | 10 | External editor launch contract |
| Interfaces/ | `IIterationService` | interface | 40 | Iteration detection & process config |
| Interfaces/ | `IPendingChangeStore` | interface | 16 | Pending change persistence |
| Interfaces/ | `IProcessConfigurationProvider` | interface | 12 | Process configuration factory |
| Interfaces/ | `IProcessTypeStore` | interface | 25 | Process type CRUD |
| Interfaces/ | `ITransaction` | interface | 12 | Opaque transaction token |
| Interfaces/ | `IUnitOfWork` | interface | 14 | Unit of work pattern |
| Interfaces/ | `IWorkItemRepository` | interface | 25 | Local cache repository |
| ReadModels/ | `Workspace` | class (sealed) | 95 | Composite read model: context + sprint + seeds |
| ReadModels/ | `WorkTree` | class (sealed) | 90 | Navigation tree read model |
| Services/ | `ConflictResolver` | static class | 105 | Three-way merge conflict detection |
| Services/ | `DynamicProcessConfigProvider` | class (sealed) | 55 | Merges hardcoded + dynamic process config |
| Services/ | `HardCodedProcessConfigProvider` | class (sealed) | 15 | Trivial DI wrapper for ProcessConfiguration |
| Services/ | `PatternMatcher` | static class | 60 | ID or substring matching |
| Services/ | `MatchResult` | abstract record | 15 | Discriminated union for match outcomes |
| Services/ | `SeedFactory` | static class | 80 | Creates seed work items with validation |
| Services/ | `StateTransitionService` | static class | 80 | Validates and classifies state transitions |
| Services/ | `TransitionResult` | record | 15 | Transition evaluation result |
| ValueObjects/ | `AreaPath` | record struct | 35 | Validated area path with cached segments |
| ValueObjects/ | `FieldChange` | record struct | 6 | Immutable field change record |
| ValueObjects/ | `IconSet` | static class | 55 | Icon glyph mappings (Unicode & Nerd Font) |
| ValueObjects/ | `IterationPath` | record struct | 35 | Validated iteration path |
| ValueObjects/ | `PendingNote` | record struct | 5 | Uncommitted comment DTO |
| ValueObjects/ | `ProcessConfigurationData` | class (sealed) | 20 | Domain-level backlog hierarchy DTO |
| ValueObjects/ | `BacklogLevelConfiguration` | class (sealed) | 10 | Single backlog level |
| ValueObjects/ | `StateShorthand` | static class | 140 | Maps p/c/s/d/x to full state names |
| ValueObjects/ | `WorkItemType` | record struct | 85 | Known work item type with validation |
| ValueObjects/ | `WorkItemTypeAppearance` | record (sealed) | 12 | Type appearance metadata |
| ValueObjects/ | `WorkItemTypeWithStates` | class (sealed) | 15 | Type + ordered states for init/refresh |
| ValueObjects/ | `WorkItemTypeState` | class (sealed) | 10 | Single state with category |

**Domain total: 42 types, ~1,850 lines**

---

### CLI Layer (`src/Twig/`)

| Subdirectory | Type | Kind | ~Lines | Description |
|---|---|---|---|---|
| (root) | `Program.cs` | top-level + classes | 200 | DI composition, ConsoleAppFramework setup, ExceptionFilter |
| (root) | `ExceptionFilter` | class (sealed) | 20 | Global exception handler filter |
| (root) | `ExceptionHandler` | static class | 40 | Maps exceptions to exit codes |
| (root) | `VersionHelper` | static class | ~10 | Assembly version extraction |
| (root) | `LegacyDbMigrator` | static class | 50 | Migrates flat DB to multi-context path |
| Commands/ | `InitCommand` | class (sealed) | 310 | `twig init` — workspace initialization |
| Commands/ | `RefreshCommand` | class (sealed) | 200 | `twig refresh` — fetch latest from ADO |
| Commands/ | `SetCommand` | class (sealed) | 95 | `twig set` — set active work item |
| Commands/ | `StatusCommand` | class (sealed) | 75 | `twig status` — display active item |
| Commands/ | `StateCommand` | class (sealed) | 150 | `twig state` — change state with shorthand |
| Commands/ | `TreeCommand` | class (sealed) | 55 | `twig tree` — hierarchy display |
| Commands/ | `NavigationCommands` | class (sealed) | 95 | `twig up/down` — tree navigation |
| Commands/ | `SeedCommand` | class (sealed) | 95 | `twig seed` — create child item |
| Commands/ | `NoteCommand` | class (sealed) | 85 | `twig note` — add comment |
| Commands/ | `UpdateCommand` | class (sealed) | 115 | `twig update` — update field + push |
| Commands/ | `EditCommand` | class (sealed) | 120 | `twig edit` — editor-based field edit |
| Commands/ | `SaveCommand` | class (sealed) | 125 | `twig save` — push pending changes |
| Commands/ | `WorkspaceCommand` | class (sealed) | 75 | `twig workspace` — sprint view |
| Commands/ | `ConfigCommand` | class (sealed) | 80 | `twig config` — read/write config |
| Commands/ | `PromptCommand` | class (sealed, internal) | 175 | `twig prompt` — shell prompt integration |
| Commands/ | `PromptData` | record struct (internal) | 12 | Prompt output DTO |
| Commands/ | `GitBranchReader` | static class (internal) | 30 | Reads git HEAD for branch name |
| Commands/ | `JsonConflictFormatter` | static class (internal) | 50 | Shared JSON conflict serialization |
| Commands/ | `ConsoleInput` | class (sealed, internal) | 12 | Default IConsoleInput implementation |
| Commands/ | `EditorLauncher` | class (sealed) | 120 | $VISUAL/$EDITOR/$GIT_EDITOR chain |
| Commands/ | `EditorNotFoundException` | class (sealed) | 5 | No editor found exception |
| Formatters/ | `IOutputFormatter` | interface | 20 | Output formatting contract |
| Formatters/ | `HumanOutputFormatter` | class (sealed) | 280 | ANSI color formatter |
| Formatters/ | `JsonOutputFormatter` | class (sealed) | 250 | JSON output formatter |
| Formatters/ | `MinimalOutputFormatter` | class (sealed) | 180 | Pipe-friendly minimal formatter |
| Formatters/ | `OutputFormatterFactory` | class (sealed) | 15 | Resolves formatter by name |
| Formatters/ | `FormatterHelpers` | static class | 25 | Shared state→shorthand mapping |
| Formatters/ | `HexToAnsi` | static class | 30 | Hex color → ANSI escape converter |
| Hints/ | `HintEngine` | class (sealed) | 120 | Contextual hint provider |

**CLI total: 34 types, ~3,350 lines**

---

### Infrastructure Layer (`src/Twig.Infrastructure/`)

| Subdirectory | Type | Kind | ~Lines | Description |
|---|---|---|---|---|
| Ado/ | `AdoRestClient` | class (sealed, internal) | 200 | IAdoWorkItemService via REST |
| Ado/ | `AdoIterationService` | class (sealed, internal) | 260 | IIterationService via REST |
| Ado/ | `AdoResponseMapper` | static class (internal) | 190 | DTO→domain mapping (anti-corruption) |
| Ado/ | `AdoErrorHandler` | static class (internal) | 110 | HTTP error → typed exceptions |
| Ado/Dtos/ | `AdoWorkItemResponse` | class (sealed, internal) | 30 | Work item response DTO |
| Ado/Dtos/ | `AdoRelation` | class (sealed, internal) | 15 | Relation link DTO |
| Ado/Dtos/ | `AdoBatchWorkItemResponse` | class (sealed, internal) | 10 | Batch response wrapper |
| Ado/Dtos/ | `AdoWiqlRequest` | class (sealed, internal) | 8 | WIQL query request |
| Ado/Dtos/ | `AdoWiqlResponse` | class (sealed, internal) | 15 | WIQL query response |
| Ado/Dtos/ | `AdoCommentRequest` | class (sealed, internal) | 8 | Comment creation request |
| Ado/Dtos/ | `AdoPatchOperation` | class (sealed, internal) | 12 | JSON Patch operation |
| Ado/Dtos/ | `AdoIterationListResponse` | class (sealed, internal) | 10 | Iteration list wrapper |
| Ado/Dtos/ | `AdoIterationResponse` | class (sealed, internal) | 10 | Single iteration DTO |
| Ado/Dtos/ | `AdoWorkItemTypeListResponse` | class (sealed, internal) | 10 | Type list wrapper |
| Ado/Dtos/ | `AdoWorkItemTypeResponse` | class (sealed, internal) | 25 | Work item type DTO |
| Ado/Dtos/ | `AdoWorkItemTypeIconResponse` | class (sealed, internal) | 10 | Type icon DTO |
| Ado/Dtos/ | `AdoWorkItemStateColor` | class (sealed, internal) | 12 | State + color + category DTO |
| Ado/Dtos/ | `AdoErrorResponse` | class (sealed, internal) | 8 | Error response DTO |
| Ado/Dtos/ | `AdoTeamFieldValuesResponse` | class (sealed, internal) | 15 | Team area path response |
| Ado/Dtos/ | `AdoConnectionDataResponse` | class (sealed, internal) | 15 | Connection data response |
| Ado/Dtos/ | `AdoProcessConfigurationResponse` | class (sealed, internal) | 25 | Process configuration response |
| Ado/Dtos/ | `AdoCategoryConfiguration` | class (sealed, internal) | 15 | Backlog category DTO |
| Ado/Dtos/ | `AdoWorkItemTypeRef` | class (sealed, internal) | 10 | Type reference DTO |
| Ado/Exceptions/ | `AdoException` | class | 8 | Base ADO exception |
| Ado/Exceptions/ | `AdoOfflineException` | class (sealed) | 8 | Network unreachable |
| Ado/Exceptions/ | `AdoBadRequestException` | class (sealed) | 5 | 400 error |
| Ado/Exceptions/ | `AdoAuthenticationException` | class (sealed) | 10 | 401 error |
| Ado/Exceptions/ | `AdoNotFoundException` | class (sealed) | 15 | 404 error |
| Ado/Exceptions/ | `AdoConflictException` | class (sealed) | 15 | 412 concurrency conflict |
| Ado/Exceptions/ | `AdoRateLimitException` | class (sealed) | 12 | 429 rate limit |
| Ado/Exceptions/ | `AdoServerException` | class (sealed) | 15 | 5xx server error |
| Auth/ | `AzCliAuthProvider` | class (sealed, internal) | 110 | Azure CLI token provider |
| Auth/ | `PatAuthProvider` | class (sealed, internal) | 60 | PAT-based auth provider |
| Config/ | `TwigConfiguration` | class (sealed) | 140 | .twig/config POCO with load/save |
| Config/ | `AuthConfig` | class (sealed) | 5 | Auth sub-config |
| Config/ | `DefaultsConfig` | class (sealed) | 10 | Defaults sub-config |
| Config/ | `AreaPathEntry` | class (sealed) | 8 | Area path with IncludeChildren |
| Config/ | `SeedConfig` | class (sealed) | 8 | Seed sub-config |
| Config/ | `DisplayConfig` | class (sealed) | 10 | Display sub-config |
| Config/ | `TypeAppearanceConfig` | class (sealed) | 8 | Type appearance entry |
| Config/ | `UserConfig` | class (sealed) | 6 | User sub-config |
| Config/ | `TwigPaths` | class (sealed) | 65 | Multi-context path resolution |
| Persistence/ | `SqliteCacheStore` | class (sealed) | 155 | SQLite lifecycle, schema, WAL mode |
| Persistence/ | `SqliteWorkItemRepository` | class (sealed) | 200 | IWorkItemRepository implementation |
| Persistence/ | `SqliteContextStore` | class (sealed) | 65 | IContextStore implementation |
| Persistence/ | `SqlitePendingChangeStore` | class (sealed) | 110 | IPendingChangeStore implementation |
| Persistence/ | `SqliteProcessTypeStore` | class (sealed) | 120 | IProcessTypeStore implementation |
| Persistence/ | `SqliteUnitOfWork` | class (sealed) | 60 | IUnitOfWork implementation |
| Persistence/ | `SqliteTransactionWrapper` | class (sealed, internal) | 15 | ITransaction wrapper |
| Serialization/ | `TwigJsonContext` | class (sealed, partial) | 55 | AOT-compatible JSON source gen |

**Infrastructure total: 49 types, ~2,350 lines**

**Grand total: 125 types, ~7,550 lines**

---

## 2. Redundancies Found

### R-001: State Category Mapping Duplicated 3x ⚠️ CRITICAL

Three independent switch expressions map ADO state names to categories. All use the same heuristic pattern but with slightly different output formats.

| Location | Output | Code Pattern |
|---|---|---|
| [FormatterHelpers.cs](src/Twig/Formatters/FormatterHelpers.cs#L14-L25) | `p/c/s/d/x` shorthand char | `state.ToLowerInvariant() switch { "new" or "to do" or "proposed" => "p", ... }` |
| [HumanOutputFormatter.cs](src/Twig/Formatters/HumanOutputFormatter.cs#L253-L265) | ANSI color escape | `state.ToLowerInvariant() switch { "closed" or "done" or "resolved" => Green, ... }` |
| [PromptCommand.cs](src/Twig/Commands/PromptCommand.cs#L111-L124) | Category string | `state.ToLowerInvariant() switch { "new" or "to do" or "proposed" => "Proposed", ... }` |

**Root cause**: ADO provides authoritative state categories via `WorkItemTypeState.Category`, but `InitCommand` and `RefreshCommand` discard them when saving to `ProcessTypeRecord`:
```
var stateNames = wit.States.Select(s => s.Name).ToList();
```
Only names are saved; categories are lost at the persistence boundary. See [InitCommand.cs](src/Twig/Commands/InitCommand.cs#L247) and [RefreshCommand.cs](src/Twig/Commands/RefreshCommand.cs#L176).

---

### R-002: Type Color Resolution Duplicated 2x

| Location | Method | Behavior |
|---|---|---|
| [HumanOutputFormatter.cs](src/Twig/Formatters/HumanOutputFormatter.cs#L233-L252) | `GetTypeColor()` | Checks `_typeColors` (hex→ANSI), then hardcoded switch, then deterministic hash |
| [PromptCommand.cs](src/Twig/Commands/PromptCommand.cs#L146-L155) | `ResolveColor()` | Checks `display.typeColors`, then falls back to `TypeAppearances` list |

Both produce hex color strings from the same config data, but `HumanOutputFormatter` also applies ANSI conversion and a hardcoded fallback chain. `PromptCommand` returns raw hex for shell theme consumers.

---

### R-003: Icon/Badge Resolution in 3 Places

| Location | Method | Fallback for unknown types |
|---|---|---|
| [IconSet.cs](src/Twig.Domain/ValueObjects/IconSet.cs#L53-L54) | `GetIcon()` | `"·"` (middle dot) |
| [HumanOutputFormatter.cs](src/Twig/Formatters/HumanOutputFormatter.cs#L267-L280) | `GetTypeBadge()` | First character of type name uppercased, or `"■"` |
| [PromptCommand.cs](src/Twig/Commands/PromptCommand.cs#L97-L98) | Uses `IconSet.GetIcon()` directly | `"·"` (middle dot) |

`HumanOutputFormatter.GetTypeBadge()` is a **hardcoded duplicate** of `IconSet.UnicodeIcons` with different fallback behavior. It always uses Unicode glyphs regardless of the `display.icons` config setting. Meanwhile, `HumanOutputFormatter` also has a `GetTypeIcon()` method that properly delegates to `IconSet.GetIcon()` but it is **never called** — `GetTypeBadge()` is called instead in all rendering paths (`FormatWorkItem`, `FormatTree`, `FormatWorkspace`, `FormatSprintView`).

---

### R-004: Conflict Resolution l/r/a Flow Duplicated 3x

The conflict detection and interactive resolution pattern (`fetch remote → ConflictResolver.Resolve → JSON format check → l/r/a prompt → branch on choice`) is near-identical in three commands:

| Location | Lines | Distinct behavior |
|---|---|---|
| [StateCommand.cs](src/Twig/Commands/StateCommand.cs#L89-L116) | 28 lines | On `r`: saves remote, prints update message |
| [UpdateCommand.cs](src/Twig/Commands/UpdateCommand.cs#L55-L82) | 28 lines | On `r`: saves remote, prints update message |
| [SaveCommand.cs](src/Twig/Commands/SaveCommand.cs#L56-L83) | 28 lines | On `r`: clears pending + saves remote |

The only variation is SaveCommand's additional `ClearChangesAsync` on remote-accept. All three share the exact same JSON formatting path via `JsonConflictFormatter`.

---

### R-005: Auto-Push Notes Pattern Duplicated 2x

| Location | Lines | Pattern |
|---|---|---|
| [StateCommand.cs](src/Twig/Commands/StateCommand.cs#L123-L135) | 13 lines | Loop pending changes → filter notes → `AddCommentAsync` → `ClearChangesByTypeAsync("note")` |
| [UpdateCommand.cs](src/Twig/Commands/UpdateCommand.cs#L90-L102) | 13 lines | Identical pattern |

Both iterate `pendingChangeStore.GetChangesAsync()`, filter for `changeType == "note"`, push via `adoService.AddCommentAsync()`, then clear notes via `ClearChangesByTypeAsync`.

---

### R-006: Process Type Build+Persist Logic Duplicated 2x

| Location | Lines | Note |
|---|---|---|
| [InitCommand.cs](src/Twig/Commands/InitCommand.cs#L232-L260) | 28 lines | Fetches types with states, fetches process config, calls `InferParentChildMap`, builds `ProcessTypeRecord`, saves |
| [RefreshCommand.cs](src/Twig/Commands/RefreshCommand.cs#L157-L193) | 36 lines | Identical flow; even calls `InitCommand.InferParentChildMap` as a static method |

`RefreshCommand` already depends on `InitCommand.InferParentChildMap` as a static method, proving the logic belongs in a shared service.

---

### R-007: TypeAppearances Dual Storage *(NEW)*

Type appearance data is stored redundantly in the config file:

| Field | Location | Set by |
|---|---|---|
| `config.TypeAppearances` | Top-level list of `TypeAppearanceConfig` | `InitCommand`, `RefreshCommand` |
| `config.Display.TypeColors` | Dictionary `{typeName → hexColor}` | `InitCommand`, `RefreshCommand` (bridged from TypeAppearances) |

Both are written simultaneously with a bridge comment (`ITEM-157`). `HumanOutputFormatter` reads `Display.TypeColors`. `PromptCommand.ResolveColor()` checks `Display.TypeColors` first, then falls back to `TypeAppearances`. The `TypeAppearances` list exists primarily for `IconId` which `Display.TypeColors` doesn't carry.

---

### R-008: HintEngine State-Done Check *(NEW)*

[HintEngine.cs](src/Twig/Hints/HintEngine.cs#L72-L82) contains a 4th instance of state-to-category logic:
```csharp
var state = sibling.State.ToLowerInvariant();
if (state is not ("closed" or "done" or "resolved" or "removed"))
```
This is a subset of the R-001 pattern (checking if a state is "completed" or "removed").

---

## 3. Domain Promotion Candidates

### 3.1 `StateCategory` Enum *(HIGH PRIORITY)*

**Current**: Three CLI-layer switch expressions hardcode state→category guesses.
**Proposed**: `Twig.Domain/Enums/StateCategory.cs` with values: `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`.

A domain service or static method would resolve category from stored process type data (using the ADO `WorkItemTypeState.Category` that is currently discarded), with the existing hardcoded switch as a fallback for legacy/offline databases.

---

### 3.2 `IconMode` Enum

**Current**: `DisplayConfig.Icons` is a `string` (`"unicode"` or `"nerd"`). `IconSet.GetIcons()` accepts a `string` and uses `string.Equals`.
**Proposed**: `Twig.Domain/Enums/IconMode.cs` with values: `Unicode`, `NerdFont`. Eliminates magic strings and provides compile-time safety.

---

### 3.3 `ConflictResolutionHelper` / `ConflictResolutionFlow`

**Current**: R-004 — the 28-line l/r/a conflict resolution flow is copy-pasted across StateCommand, UpdateCommand, SaveCommand.
**Proposed**: A CLI-layer helper (not domain — it involves console I/O) that encapsulates:
1. `ConflictResolver.Resolve(local, remote)`
2. JSON format branching
3. Interactive l/r/a prompt
4. Remote-accept handling

Returns a discriminated result: `Proceed`, `AcceptedRemote`, `Aborted`.

---

### 3.4 `ProcessTypeSyncService`

**Current**: R-006 — InitCommand and RefreshCommand both manually fetch type states, fetch process config, infer parent-child maps, build `ProcessTypeRecord` objects, and persist them.
**Proposed**: A domain service (or CLI-layer application service) that encapsulates:
1. `IIterationService.GetWorkItemTypesWithStatesAsync()`
2. `IIterationService.GetProcessConfigurationAsync()`
3. `InferParentChildMap()` (currently a static method on InitCommand)
4. `ProcessTypeRecord` construction and persistence via `IProcessTypeStore`

---

### 3.5 `AutoPushNotesHelper`

**Current**: R-005 — StateCommand and UpdateCommand both loop pending changes, filter notes, push via API.
**Proposed**: A CLI-layer helper or an application-service method that takes `(int workItemId, IPendingChangeStore, IAdoWorkItemService)` and returns `bool hadNotes`.

---

### 3.6 `StateCategoryResolver` Domain Service

**Current**: State category resolution is scattered.
**Proposed**: `Twig.Domain/Services/StateCategoryResolver.cs`:
```
StateCategory Resolve(string stateName, string? typeName, IProcessTypeStore store)
```
Checks stored process type data first (with ADO categories), falls back to hardcoded heuristic.

---

## 4. Layer Violations

### LV-001: PromptCommand Raw SQLite Access

[PromptCommand.cs](src/Twig/Commands/PromptCommand.cs#L60-L100) opens a direct `SqliteConnection` with `Mode=ReadOnly`, bypassing all domain interfaces (`IWorkItemRepository`, `IContextStore`). This is **intentional** (NFR-004: no stderr, 100ms budget), but it creates a parallel data access path that must be maintained alongside the repository abstractions.

**Schema coupling**: PromptCommand hardcodes table/column names (`context`, `work_items`, `active_work_item_id`, `id`, `type`, `title`, `state`, `is_dirty`). Any schema change requires updating both `SqliteCacheStore.Ddl` and PromptCommand.

---

### LV-002: State-to-Category Logic in CLI/Formatter Layer

**FormatterHelpers.GetShorthand**, **HumanOutputFormatter.GetStateColor**, **PromptCommand.GetStateCategory**, and **HintEngine** all embed domain knowledge about which state names map to which categories. This is business logic that should live in the Domain layer, potentially driven by stored ADO category data.

---

### LV-003: Type-to-Color Logic in Formatter Layer

[HumanOutputFormatter.GetTypeColor()](src/Twig/Formatters/HumanOutputFormatter.cs#L233-L252) has a hardcoded switch mapping type names to ANSI colors. This embeds domain knowledge about which types are "Epic-like" (magenta), "Bug-like" (red), etc. The config-driven path (`Display.TypeColors` → `HexToAnsi`) is correct architecture; the hardcoded fallback belongs in a domain-level default mapping.

---

### LV-004: InferParentChildMap in InitCommand

[InitCommand.InferParentChildMap()](src/Twig/Commands/InitCommand.cs#L290-L315) is a `static` method on a CLI command class. It implements domain logic (backlog hierarchy → parent-child inference) that has no dependency on CLI concerns. It should live in `Twig.Domain/Services/`.

**Evidence**: `RefreshCommand` already calls `InitCommand.InferParentChildMap(processConfig)` — a command calling a static method on another command is a code smell indicating misplaced logic.

---

### LV-005: RefreshCommand Injects SqliteCacheStore Directly

[RefreshCommand](src/Twig/Commands/RefreshCommand.cs#L21) takes `SqliteCacheStore cacheStore` as a constructor parameter (alongside the proper `IWorkItemRepository`, `IContextStore` etc.). It uses `cacheStore` to construct a new `SqliteProcessTypeStore(cacheStore)` inline. This bypasses the DI container — `IProcessTypeStore` is already registered in `Program.cs`.

---

### LV-006: InitCommand Constructs Infrastructure Types Directly

[InitCommand](src/Twig/Commands/InitCommand.cs#L225) creates `new SqliteCacheStore(...)` and `new SqliteProcessTypeStore(cacheStore)` directly, coupling the CLI command to infrastructure concrete types. Unlike other commands that use DI-injected interfaces, InitCommand must construct its own because the DB may not exist yet at DI resolution time.

---

### LV-007: Config Types Contain Display Knowledge

`DisplayConfig.TypeColors` (in Infrastructure/Config) carries rendering data — hex color strings keyed by type name. While config is inherently about display, the **bridge logic** that populates `TypeColors` from `TypeAppearances` lives in [InitCommand](src/Twig/Commands/InitCommand.cs#L158-L161) and [RefreshCommand](src/Twig/Commands/RefreshCommand.cs#L145-L148), coupling command code to config structure.

---

## 5. Code Smells

### CS-001: InitCommand is Too Large (310 lines)

`InitCommand.ExecuteAsync` is a ~230-line method that handles:
- Directory creation
- Config file writing
- Process template detection
- Type appearance fetching
- Area path detection
- Iteration detection
- User identity detection
- State sequence fetching
- Process configuration fetching
- Parent-child map inference
- ProcessTypeRecord construction + persistence
- .gitignore management
- Hint generation

**Recommendation**: Extract process type sync (R-006) and parent-child inference (LV-004) into services.

---

### CS-002: HumanOutputFormatter is Large (280 lines) with Two Badge Methods

`HumanOutputFormatter` has both `GetTypeIcon()` (which delegates to `IconSet`) and `GetTypeBadge()` (hardcoded duplicate). `GetTypeIcon()` is `internal` and **never called** from any rendering method — all paths call `GetTypeBadge()`.

---

### CS-003: ProcessConfiguration is Large (270 lines) with 4 Hardcoded Templates

`ProcessConfiguration` contains `BuildBasic()`, `BuildAgile()`, `BuildScrum()`, `BuildCmmi()` — each ~30 lines of state/type boilerplate. This is by design (RD-003: domain knowledge) but could be data-driven with the dynamic process configuration already available.

---

### CS-004: HumanOutputFormatter Constructor Confusion

Two constructors exist:
1. `HumanOutputFormatter(DisplayConfig displayConfig)` — uses `displayConfig.Icons` to select icon set
2. `HumanOutputFormatter(Dictionary<string, string>? typeColors)` — hardcodes `"unicode"` mode

The second constructor exists for tests but creates a silent inconsistency: tests always run in unicode mode regardless of what they intend to test.

---

### CS-005: Dead Method — `GetTypeIcon()`

`HumanOutputFormatter.GetTypeIcon(WorkItemType type)` at line ~53 is declared `internal` but has **zero call sites** across the entire codebase. It was probably intended to replace `GetTypeBadge()` but was never wired in.

---

### CS-006: AdoIterationService Makes Redundant HTTP Calls

`DetectProcessTemplateAsync()` and `GetWorkItemTypeAppearancesAsync()` and `GetWorkItemTypesWithStatesAsync()` all call the same `_apis/wit/workitemtypes` endpoint independently. During `twig init`, three identical HTTP requests are made. The code has a comment acknowledging this inefficiency.

---

### CS-007: Sync-over-Async in DynamicProcessConfigProvider

`DynamicProcessConfigProvider.GetConfiguration()` calls `.GetAwaiter().GetResult()` on `_processTypeStore.GetAllAsync()`. This is documented as CLI-safe and is pragmatically correct for the single-threaded console app, but it's a pattern that should not be replicated.

---

## 6. Configuration Analysis

### 6.1 TypeAppearances Dual Storage (R-007)

The config file stores type appearance data in two places:

```json
{
  "typeAppearances": [
    { "name": "Epic", "color": "009CCC", "iconId": "icon_crown" },
    ...
  ],
  "display": {
    "typeColors": {
      "Epic": "009CCC",
      ...
    }
  }
}
```

**`typeAppearances`**: Full appearance data including `iconId`. Set by Init/Refresh.
**`display.typeColors`**: Color-only subset. Bridged from `typeAppearances` by Init/Refresh. Read by `HumanOutputFormatter`.

This creates a consistency hazard: if `display.typeColors` is manually edited by a user (via `twig config`), it diverges from `typeAppearances`. The next `twig refresh` silently overwrites the user's customization.

### 6.2 Domain Knowledge in Config POCOs

`DisplayConfig.Icons` carries the mode string (`"unicode"` or `"nerd"`) validated only by `TwigConfiguration.SetValue()` switch. There is no enum type — validation is scattered across:
- `TwigConfiguration.SetValue()` (Infrastructure)
- `IconSet.GetIcons()` (Domain)
- `HumanOutputFormatter` constructor (CLI)

### 6.3 SeedConfig.DefaultChildType

`SeedConfig.DefaultChildType` is a `Dictionary<string, string>?` that appears to be unused — `SeedFactory.Create()` infers child types from `ProcessConfiguration`, not from config.

---

## 7. Prioritized Recommendations

### Theme A: State Category Consolidation *(HIGH impact, LOW risk)*

**Problem**: ADO provides authoritative state categories but they are discarded at the persistence boundary. Three CLI-layer switches guess categories from state names, breaking for custom states, inherited processes, and custom templates.

| ID | Change | Risk | Files affected |
|---|---|---|---|
| A-1 | Add `StateCategory` enum to `Twig.Domain/Enums/` | None | New file |
| A-2 | Extend `ProcessTypeRecord` to store state categories (e.g., `IReadOnlyList<StateEntry>` with `Name` + `Category`) | Low | `ProcessTypeRecord.cs` |
| A-3 | Add `state_categories_json` column to `process_types` table (or restructure `states_json` to include categories). Bump `SchemaVersion` to 4. | Low | `SqliteCacheStore.cs`, `SqliteProcessTypeStore.cs` |
| A-4 | Fix `InitCommand` and `RefreshCommand` to persist categories alongside state names | Low | `InitCommand.cs`, `RefreshCommand.cs` |
| A-5 | Create `StateCategoryResolver` domain service with stored-data lookup + hardcoded fallback | Low | New file in `Services/` |
| A-6 | Refactor `FormatterHelpers.GetShorthand()`, `HumanOutputFormatter.GetStateColor()`, `PromptCommand.GetStateCategory()`, `HintEngine` to delegate to resolver | Low | 4 files |
| A-7 | Handle DB migration: old DBs without category data degrade gracefully to hardcoded fallback | Low | `StateCategoryResolver` |
| A-8 | Consider using ADO state colors for state rendering (not just type colors) | Low | Future enhancement |

**Constraint**: PromptCommand's raw SQLite access is intentional for perf. After A-3, its SQL query may need updating to read from the new schema, but the architecture (raw SQLite, no stderr) should remain.

---

### Theme B: Icon/Badge Consolidation *(HIGH impact, LOW risk)*

| ID | Change | Risk |
|---|---|---|
| B-1 | Wire `GetTypeIcon()` into all `HumanOutputFormatter` rendering paths, replacing `GetTypeBadge()` calls | Low |
| B-2 | Delete `GetTypeBadge()` (now dead code) | Low |
| B-3 | Create `IconMode` enum in `Twig.Domain/Enums/` | None |
| B-4 | Update `DisplayConfig.Icons` to serialize/deserialize via `IconMode` enum | Low |
| B-5 | Verify PromptCommand already uses `IconSet.GetIcon()` correctly (it does) | None |

---

### Theme C: Conflict Resolution Extraction *(MEDIUM impact, LOW risk)*

| ID | Change | Risk |
|---|---|---|
| C-1 | Create `ConflictResolutionFlow` helper in `Commands/` with: resolve → json-or-prompt → return action | Low |
| C-2 | Refactor `StateCommand`, `UpdateCommand`, `SaveCommand` to call the helper | Low |
| C-3 | Add `SaveCommand`-specific `clearPending` parameter to the helper or use callback | Low |

---

### Theme D: Process Type Sync Extraction *(MEDIUM impact, LOW risk)*

| ID | Change | Risk |
|---|---|---|
| D-1 | Move `InferParentChildMap` from `InitCommand` to `Twig.Domain/Services/BacklogHierarchyService.cs` | Low |
| D-2 | Create `ProcessTypeSyncService` that encapsulates fetch-infer-build-persist flow | Low |
| D-3 | Refactor `InitCommand` and `RefreshCommand` to delegate to the service | Low |
| D-4 | Address `AdoIterationService` triple HTTP call (cache `workitemtypes` response) | Low |

---

### Theme E: Type Color Consolidation *(MEDIUM impact, MEDIUM risk)*

| ID | Change | Risk |
|---|---|---|
| E-1 | Remove hardcoded type→color switch from `HumanOutputFormatter.GetTypeColor()` | Medium (may change colors for users without config) |
| E-2 | Ensure `Display.TypeColors` is always populated from ADO data at init/refresh (already done) | Low |
| E-3 | Move deterministic color hash to domain as a fallback for unknown types | Low |
| E-4 | Resolve dual storage (R-007): derive `Display.TypeColors` from `TypeAppearances` at read time instead of duplicating in config | Medium |

---

### Theme F: PromptCommand Architecture *(MEDIUM impact, HIGH risk)*

| ID | Change | Risk |
|---|---|---|
| F-1 | After Theme A, update PromptCommand's SQL to read state categories from the new schema column | Medium |
| F-2 | Consider a lightweight `IPromptDataProvider` interface for testability (optional) | Medium |
| F-3 | Document the raw-SQLite-for-perf design decision as an ADR | None |

**Constraint**: PromptCommand must remain fast (<100ms), must not write to stderr, and must not use DI-resolved repositories. Any changes must preserve these invariants.

---

### Theme G: Split Large Files *(LOW impact, LOW risk)*

| ID | Change | Risk |
|---|---|---|
| G-1 | Extract `InitCommand` helper logic (gitignore, directory creation) into `InitHelpers` | Low |
| G-2 | Extract `HumanOutputFormatter` rendering submethods (FormatTree, FormatWorkspace, FormatSprintView) into partial class files if desired | Low |
| G-3 | Extract `ProcessConfiguration.Build*()` methods into separate files per template (optional — these are stable) | Very low |

---

### Theme H: Configuration Cleanup *(LOW impact, MEDIUM risk)*

| ID | Change | Risk |
|---|---|---|
| H-1 | Remove `TypeAppearances` top-level list; derive from `Display.TypeColors` + a new `Display.TypeIcons` dictionary | Medium (migration) |
| H-2 | Or: remove `Display.TypeColors` bridge; have `HumanOutputFormatter` read `TypeAppearances` directly | Medium |
| H-3 | Audit `SeedConfig.DefaultChildType` — appears unused, consider removing | Low |
| H-4 | Add config validation at load time (warn on unknown keys) | Low |

---

### Theme I: DI Consistency *(LOW impact, LOW risk)*

| ID | Change | Risk |
|---|---|---|
| I-1 | Have `RefreshCommand` accept `IProcessTypeStore` via DI instead of constructing `SqliteProcessTypeStore` from `SqliteCacheStore` | Low |
| I-2 | Consider a `Func<string, SqliteCacheStore>` factory for `InitCommand` to avoid direct infrastructure coupling | Low |
| I-3 | Remove the `HumanOutputFormatter(Dictionary<string, string>?)` constructor; have tests use `new HumanOutputFormatter(new DisplayConfig { ... })` | Low |

---

## Summary Matrix

| Theme | Impact | Risk | Redundancies Resolved | Layer Violations Fixed | Approx. Effort |
|---|---|---|---|---|---|
| **A: State Category** | 🔴 High | 🟢 Low | R-001, R-008 | LV-002 | 8–13 days |
| **B: Icon/Badge** | 🔴 High | 🟢 Low | R-003 | — | 1–2 days |
| **C: Conflict Resolution** | 🟡 Medium | 🟢 Low | R-004 | — | 2–3 days |
| **D: Process Sync** | 🟡 Medium | 🟢 Low | R-006 | LV-004, LV-005 | 3–4 days |
| **E: Type Color** | 🟡 Medium | 🟡 Medium | R-002, R-007 | LV-003 | 3–5 days |
| **F: PromptCommand** | 🟡 Medium | 🔴 High | — | LV-001 | 2–3 days |
| **G: Split Files** | 🟢 Low | 🟢 Low | — | — | 1–2 days |
| **H: Config Cleanup** | 🟢 Low | 🟡 Medium | R-007 | LV-007 | 2–3 days |
| **I: DI Consistency** | 🟢 Low | 🟢 Low | — | LV-005, LV-006 | 1–2 days |

**Recommended execution order**: B → A → C → D → I → E → G → H → F

Theme B is the quickest win (wire the existing `GetTypeIcon()` method, delete `GetTypeBadge()`). Theme A is the highest-impact structural fix — it resolves the core data-loss issue where ADO categories are discarded. Themes C and D are clean extraction refactors with no behavioral risk. Theme F should be last because PromptCommand's architecture constraints make changes risky.
