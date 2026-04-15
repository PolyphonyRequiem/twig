# Command UX & Ergonomics

> **Status**: 🔨 In Progress — 1/2 PR groups merged | **Epic**: #1526 | **Revision**: R6 — Added PR Groups section (readability review); fixed stale FR-15 references in child/sibling 'Not Included' callouts; renamed 'Open Questions' to 'Design Q&A' (all resolved); fixed missing space in Data Flow header; added test case for simultaneous `--title` + trailing positional text (T-1527-2); added NFR-05 confirming no new telemetry properties; acknowledged `SeedFactory.Create` alternative in DD-8

## Executive Summary

This plan improves the command-line user experience of the twig CLI across two areas: (1) reducing argument-quoting friction by treating trailing unquoted words as a single argument for key commands (`note`, `new`), and (2) adding smart defaults and relationship sub-commands to `twig new` so users can create child and sibling work items directly in ADO with minimal ceremony. The changes are localized to the CLI layer (`Program.cs`, command classes, DI wiring) with no modifications to the domain or infrastructure layers. All existing command invocations remain backward-compatible.

## Background

### Current UX Pain Points

The twig CLI currently requires verbose, explicit arguments for common operations:

```bash
# Creating a note — requires --text flag and quoting
twig note --text "Starting work on the feature"

# Creating a work item — requires both --title and --type as named flags
twig new --title "Fix the login page" --type Task

# Creating a child — requires two-step seed workflow
twig seed new --title "Implement validation"
twig seed publish --all
```

These patterns add friction to the most common developer workflows. In particular, `twig note` is typed dozens of times per day during active development, and the `--text "..."` ceremony is the primary ergonomic complaint.

### Architecture Context

The CLI uses ConsoleAppFramework's source-generated argument parsing with established patterns:

| Pattern | Attribute | Example |
|---------|-----------|---------|
| Positional arg | `[Argument]` | `twig set 1234` → `Set([Argument] string idOrPattern)` |
| Named flag | _(none)_ | `twig new --title "X"` → `New(string title)` |
| Rest args | `params string[]` | `twig commit "msg" --amend` → `Commit(..., params string[] passthrough)` |
| Sub-command | `[Command("x y")]` | `twig seed new` → `[Command("seed new")] SeedNew(...)` |

The rest-args pattern (`CancellationToken ct = default, params string[] rest`) is used by `Commit` and `SeedChain`. ConsoleAppFramework's source generator handles `CancellationToken` as special injection, allowing `params` to follow it.

**Critical constraint**: In ConsoleAppFramework, marking a parameter with `[Argument]` converts it from a named parameter to a positional-only parameter. This means `[Argument] string? text` would make `--text` inaccessible as a named flag, breaking backward compatibility. The design therefore uses a **params-only approach**: named parameters remain unchanged (preserving `--text`, `--title`), while `params string[]` captures any trailing positional arguments. See the Alternatives Considered section for the full analysis.

Key existing behavior: `twig new` creates and publishes directly to ADO (one step), while `twig seed new` creates a local draft (requires `seed publish` to push). The `SeedFactory.Create` method already handles type inference from parent context and area/iteration path inheritance — `twig new child` will reuse this logic.

### Call-Site Audit

Both Issues modify command signatures in `Program.cs` (the sole production call site). Changes are additive — existing named parameters retain defaults and all existing test call sites are unaffected.

| File | Method | Current Signature | Impact |
|------|--------|-------------------|--------|
| `Program.cs:483` | `Note(...)` | `string? text = null, string output = ...` | Add `params string[] textParts` (no change to `text` — it remains a named param, preserving `--text`) |
| `Program.cs:318` | `New(...)` | `string title, string type, ...` | Make `title` optional (`string? title = null`), make `type` optional (`string? type = null`), add `params string[] titleParts` |
| `Program.cs` | `NewChild(...)` | _(new)_ | New `[Command("new child")]` method |
| `Program.cs` | `NewSibling(...)` | _(new)_ | New `[Command("new sibling")]` method |
| `NewCommand.cs:22` | `ExecuteAsync(...)` | `string? title, string type, ...` | Make `type` optional (`string? type = null`); add type inference when `--parent` is provided |
| `NewCommand.cs` | `ExecuteChildAsync(...)` | _(new)_ | New method for child creation |
| `NewCommand.cs` | `ExecuteSiblingAsync(...)` | _(new)_ | New method for sibling creation |
| `CommandRegistrationModule.cs:51` | `AddCoreCommands(...)` | `services.AddSingleton<NewCommand>()` | No change — auto-wiring resolves the new required deps |

**Test call sites** (unaffected — new params default to `null`/empty):

| File | Test Methods | Impact |
|------|-------------|--------|
| `NoteCommandTests.cs` | 9 (9 `[Fact]`) | Unaffected (`text` parameter signature unchanged) |
| `NewCommandTests.cs` | 18 (17 `[Fact]` + 1 `[Theory]`) | Constructor gains 2 new params → all 18 test methods must update construction. Existing assertions remain valid since new params default to `null`. |
| `SeedNewCommandTests.cs` | 14 (13 `[Fact]` + 1 `[Theory]`) | Unaffected (separate command class) |

## Problem Statement

1. **Quoting friction on common commands**: `twig note --text "Starting work"` and `twig new --title "Fix the login page" --type Task` require explicit flag names and shell quoting for multi-word text. This adds cognitive overhead to the most frequently used twig workflows — adding notes during development and creating work items.

2. **No direct-to-ADO child creation**: Creating a child work item that's immediately published to ADO requires two commands (`seed new` → `seed publish`). There is no `twig new child` shortcut that infers type from the active context and publishes immediately — despite this being the most common creation pattern for developers working within a hierarchy.

3. **Explicit type always required for `twig new`**: Even when `--parent` is specified, the user must also provide `--type`. The process configuration already knows which child types are valid for each parent type, but `twig new` doesn't leverage this information.

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G-1 | `twig note starting work on feature` works without `--text` or quotes |
| G-2 | `twig new Fix the login page --type Task` works with title as trailing text |
| G-3 | `twig new child "Fix the login page"` creates child of active context in ADO with inferred type |
| G-4 | `twig new sibling "Another task"` creates sibling of active context in ADO |
| G-5 | `--type` is optional on `twig new` when `--parent` is provided (type inferred from process config) |
| G-6 | `--set` flag on `new child`/`new sibling` sets newly created item as active context |
| G-7 | All existing command invocations continue to work unchanged (backward compatibility) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG-1 | Changing `twig commit` trailing text behavior — `params string[] passthrough` serves dual purpose (message continuation + git flag forwarding); modifying it would break `twig commit "msg" --amend` |
| NG-2 | Adding trailing text to `twig state` — single-word state names are already well-served by `[Argument]` |
| NG-3 | Adding positional title to `twig seed new` — separate scope; `twig seed "title"` shortcut exists |
| NG-4 | Replacing `twig seed new` with `twig new child` — seed workflow remains for draft-oriented creation |
| NG-5 | Batch creation of multiple items via `twig new` |

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig note my note text` joins all trailing positional args as the note text |
| FR-02 | `twig note --text "my note text"` continues to work (backward compat) |
| FR-03 | `twig note` with no text opens the editor (current behavior preserved) |
| FR-04 | `twig new Fix the bug --type Task` treats "Fix the bug" as the title |
| FR-05 | `twig new --title "Fix the bug" --type Task` continues to work |
| FR-06 | `twig new child "Title"` creates a child of the active context, published immediately to ADO |
| FR-07 | `twig new child "Title" --type Bug` allows explicit type override on child creation |
| FR-08 | `twig new sibling "Title"` creates a sibling of the active context, published immediately to ADO |
| FR-09 | `twig new child` / `twig new sibling` inherits area/iteration from the parent context |
| FR-10 | `twig new --parent 123` without `--type` infers type from parent's allowed child types |
| FR-11 | `twig new --parent 123 --type Bug` allows explicit type override when parent is specified |
| FR-12 | `twig new child --set` and `twig new sibling --set` set the newly created item as active context |
| FR-13 | `twig new child` and `twig new sibling` without a title produce a clear error: `"Title is required. Usage: twig new child \"title\""` |
| FR-14 | `twig new child --type Bug` validates that `Bug` is an allowed child type of the parent's type before calling `CreateAsync`; `twig new sibling --type Bug` validates that `Bug` is an allowed child type of the grandparent's type |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | No reflection — all JSON serialization uses `TwigJsonContext` source generator |
| NFR-02 | AOT-compatible — no dynamic code generation |
| NFR-03 | No new external dependencies |
| NFR-04 | `TreatWarningsAsErrors` compliance across all modified files |
| NFR-05 | No new telemetry properties — `NewCommand` does not inject `ITelemetryClient`; existing command-level telemetry in `Program.cs` covers `new child`/`new sibling` via `CommandExecuted`. |

## Proposed Design

### Architecture Overview

The design adds two capabilities layered on the existing ConsoleAppFramework architecture:

1. **Trailing text joining** — a per-command pattern where `params string[]` captures trailing positional words while named parameters (e.g., `--text`, `--title`) remain accessible for backward compatibility. The named parameter takes precedence; when absent, positional args are joined. The joining logic lives in `Program.cs` (the arg-parsing boundary), keeping command classes clean.

2. **Relationship sub-commands** — new `[Command("new child")]` and `[Command("new sibling")]` methods in `TwigCommands` that delegate to expanded `NewCommand` methods. These follow the existing sub-command pattern used by `seed new`, `link parent`, `nav up`, etc.

```
Program.cs (TwigCommands)
  ├── Note(text?, ..., params textParts)              → JoinTrailingText → NoteCommand.ExecuteAsync
  ├── New(title?, type?, ..., params titleParts)      → JoinTrailingText → NewCommand.ExecuteAsync
  ├── [Command("new child")]  NewChild(title?, ...)   → JoinTrailingText → NewCommand.ExecuteChildAsync
  └── [Command("new sibling")] NewSibling(title?, ...)→ JoinTrailingText → NewCommand.ExecuteSiblingAsync
                                                             │
                                                             ▼
                                                      NewCommand
                                                       ├── ExecuteAsync (existing, enhanced)
                                                       ├── ExecuteChildAsync (new)
                                                       └── ExecuteSiblingAsync (new)
                                                             │
                                            ┌────────────────┼───────────────────┐
                                            ▼                ▼                   ▼
                                    ActiveItemResolver  ProcessConfig    SeedFactory/AdoService
                                    (context lookup)    (type inference) (create + publish)
```

> **⚠ Sub-command dispatch note**: ConsoleAppFramework uses longest-prefix matching. When `[Command("new child")]` is registered, `twig new child "my title"` routes to `NewChild()` — the token `child` is consumed as a sub-command token, not as a positional title. This means any title that literally starts with "child" or "sibling" must be passed via `--title` to avoid sub-command interception. For example, `twig new child task --type Bug` dispatches to `NewChild()` with title="task". To create a plain `twig new` item with the word "child" in the title, use: `twig new --title "child safety review" --type Task`. This is an inherent trade-off of sub-command routing and is consistent with `seed`/`seed new` behavior in the existing codebase.

### Key Components

#### 1. Trailing Text Joining (Issue #1527)

**Pattern**: Each command that supports trailing text keeps its named parameter (e.g., `text`, `title`) as a regular optional parameter — preserving `--text` / `--title` access — while adding `params string[]` to capture trailing positional arguments. The `Program.cs` wrapper resolves the effective value: the named parameter takes precedence; when absent, positional args are joined:

```csharp
// Program.cs — private helper in TwigCommands
private static string? JoinTrailingText(string? named, string[]? positional)
{
    if (named is not null)
        return named;
    if (positional is { Length: > 0 })
        return string.Join(" ", positional);
    return null;
}
```

**Note command** — current vs. proposed:

```csharp
// BEFORE: requires --text flag
public async Task<int> Note(string? text = null, string output = ..., CancellationToken ct = default)

// AFTER: --text still works as named param (FR-02); trailing words captured by params (FR-01)
public async Task<int> Note(
    string? text = null,                                    // --text "my note" preserved (G-7)
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default,
    params string[] textParts)                              // captures: twig note Hello world
{
    var effectiveText = JoinTrailingText(text, textParts);
    return await services.GetRequiredService<NoteCommand>().ExecuteAsync(effectiveText, output, ct);
}
```

**New command** — current vs. proposed:

```csharp
// BEFORE: requires --title and --type flags
public async Task<int> New(string title, string type, ...)

// AFTER: --title and --type still work as named params (FR-05, G-7); trailing words for title (FR-04)
public async Task<int> New(
    string? title = null,                                   // --title "Fix bug" preserved (G-7)
    string? type = null,                                    // optional when --parent given (FR-10)
    string? area = null, string? iteration = null, string? description = null,
    int? parent = null, bool set = false, bool editor = false,
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default,
    params string[] titleParts)                             // captures: twig new Fix the bug --type Task
{
    var effectiveTitle = JoinTrailingText(title, titleParts);
    return await services.GetRequiredService<NewCommand>()
        .ExecuteAsync(effectiveTitle, type, area, iteration, description, parent, set, editor, output, ct);
}
```

**Sub-command signatures** (Program.cs):

```csharp
/// <summary>Create a child work item of the active context.</summary>
[Command("new child")]
public async Task<int> NewChild(
    string? title = null,                                   // --title still works
    string? type = null,
    bool set = false,
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default,
    params string[] titleParts)                             // captures positional args
{
    var effectiveTitle = JoinTrailingText(title, titleParts);
    return await services.GetRequiredService<NewCommand>()
        .ExecuteChildAsync(effectiveTitle, type, set, output, ct);
}

/// <summary>Create a sibling work item of the active context.</summary>
[Command("new sibling")]
public async Task<int> NewSibling(
    string? title = null,                                   // --title still works
    string? type = null,
    bool set = false,
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default,
    params string[] titleParts)                             // captures positional args
{
    var effectiveTitle = JoinTrailingText(title, titleParts);
    return await services.GetRequiredService<NewCommand>()
        .ExecuteSiblingAsync(effectiveTitle, type, set, output, ct);
}
```

#### 2.Smart Type Inference for `twig new` (Issue #1528)

When `--type` is omitted, `NewCommand` attempts to infer the child type:

1. If `--parent` is provided, fetch the parent from cache/ADO via `ActiveItemResolver.ResolveByIdAsync(parentId)`:
   - `Found(WorkItem)` or `FetchedFromAdo(WorkItem)` → parent item (continue)
   - `Unreachable(id, reason)` → error: `"Parent item #{id} is unreachable: {reason}"` (exit 1)
2. Query `ProcessConfiguration.GetAllowedChildTypes(parentType)`
3. If exactly one child type is allowed, use it automatically
4. If multiple are allowed, use the first one (matches `SeedFactory.Create` precedent)
5. If none are allowed, error: `"Type '{parentType}' does not allow child items."`
6. If no parent context is available and `--type` is omitted, error: `"Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."`
7. Emit a discoverability hint: `"Inferred type '{childType}' from parent. Use --type to override."` (see DD-4)

**Type path convergence**: Both the explicit and inferred paths must produce a `WorkItemType` value before reaching `SeedFactory.CreateUnparented`. The explicit path parses a user-supplied string via `WorkItemType.Parse(type)`, which normalizes casing and returns `Result<WorkItemType>`. The inferred path retrieves a `WorkItemType` directly from `ProcessConfiguration.GetAllowedChildTypes()`, which already stores canonical `WorkItemType` values (built from `WorkItemType.Parse` during `ProcessConfiguration.FromRecords`). Both paths converge at a single `WorkItemType childType` local before calling `SeedFactory.CreateUnparented(title, childType, ...)`. This means the inferred path skips `Parse()` intentionally — the values are pre-validated at configuration load time.

**NewCommand constructor** — expanded with required dependencies:

```csharp
public sealed class NewCommand(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    // Added for child/sibling/inference — always registered in DI
    ActiveItemResolver activeItemResolver,
    IProcessConfigurationProvider processConfigProvider)
```

#### 3. `twig new child` Sub-Command

Creates a child of the active context item, published immediately to ADO:

```
twig new child "Fix the login page"                  # type inferred from parent
twig new child "Fix the login page" --type Bug        # explicit type override
twig new child "Fix the login page" --set             # set as active context
```

> **Not Included**: `--description` is not supported on `twig new child`. Add description after creation via `twig update System.Description "<markdown>" --format markdown`, or use the `twig seed new --editor` → `twig seed publish` workflow for editor-based creation.

**Flow**:
1. If title is null/empty, error: `"Title is required. Usage: twig new child \"title\""`
2. Resolve active context via `ActiveItemResolver.GetActiveItemAsync()` → pattern-match on `ActiveItemResult` using `TryGetWorkItem`:
   - `Found(WorkItem)` or `FetchedFromAdo(WorkItem)` → parent item (continue)
   - `NoContext` → error: `"No active work item. Run 'twig set <id>' first."` (exit 1)
   - `Unreachable(id, reason)` → error: `"Active item #{id} is unreachable: {reason}"` (exit 1)
3. `ProcessConfiguration.GetAllowedChildTypes(parent.Type)` → allowed child types
4. If `--type` provided, validate it is in the allowed children list (error: `"Type '{type}' is not an allowed child of '{parent.Type}'."`)
5. If `--type` omitted, infer from allowed children: use first allowed type (error if none allowed: `"Type '{parent.Type}' does not allow child items."`)
6. Emit discoverability hint when type was inferred: `"Inferred type '{childType}' from parent. Use --type to override."`
7. Inherit area/iteration from parent (same as `SeedFactory.Create`)
8. `SeedFactory.CreateUnparented(title, childType, parent.AreaPath, parent.IterationPath, config.User.DisplayName, parent.Id)` (consistent with `NewCommand.cs:76`)
9. `adoService.CreateAsync(seed)` → newId
10. `adoService.FetchAsync(newId)` → fetched
11. `workItemRepo.SaveAsync(fetched)`
12. If `--set`, set active context

#### 4. `twig new sibling` Sub-Command

Creates a sibling of the active context item:

```
twig new sibling "Another task"               # same type as active item
twig new sibling "Another task" --type Bug     # explicit type override
```

> **Not Included**: `--description` is not supported on `twig new sibling`. Add description after creation via `twig update System.Description "<markdown>" --format markdown`, or use the `twig seed new --editor` → `twig seed publish` workflow for editor-based creation.

**Flow**:
1. If title is null/empty, error: `"Title is required. Usage: twig new sibling \"title\""`
2. Resolve active context via `ActiveItemResolver.GetActiveItemAsync()` → pattern-match on `ActiveItemResult` using `TryGetWorkItem`:
   - `Found(WorkItem)` or `FetchedFromAdo(WorkItem)` → active item (continue)
   - `NoContext` → error: `"No active work item. Run 'twig set <id>' first."` (exit 1)
   - `Unreachable(id, reason)` → error: `"Active item #{id} is unreachable: {reason}"` (exit 1)
3. Validate `active.ParentId` is not null (error: `"Active item #{id} has no parent — cannot create sibling."`)
4. Fetch parent via `ActiveItemResolver.ResolveByIdAsync(active.ParentId.Value)`:
   - `Found(WorkItem)` or `FetchedFromAdo(WorkItem)` → parent item (continue)
   - `Unreachable(id, reason)` → error: `"Parent item #{id} is unreachable: {reason}"` (exit 1)
5. If `--type` omitted, use active item's type (natural sibling)
6. **Validate type**: Query `ProcessConfiguration.GetAllowedChildTypes(parent.Type)` and verify the resolved type (whether inferred from active item or explicit `--type`) is in the allowed list (error: `"Type '{type}' is not an allowed child of '{parent.Type}'."`)
7. Inherit area/iteration from parent
8. `SeedFactory.CreateUnparented(title, siblingType, parent.AreaPath, parent.IterationPath, config.User.DisplayName, parent.Id)` (consistent with `NewCommand.cs:76`)
9. `adoService.CreateAsync(seed)` → newId
10. `adoService.FetchAsync(newId)` → fetched
11. `workItemRepo.SaveAsync(fetched)`
12. If `--set`, set active context

### Data Flow

**Trailing text flow** (`twig note Hello world`):
```
Shell:          ["note", "Hello", "world"]
  → CAF parse: text=null (no --text flag), textParts=["Hello", "world"]
  → JoinTrailingText: named=null, positional=["Hello", "world"] → "Hello world"
  → NoteCommand.ExecuteAsync(text: "Hello world")
  → (existing note logic unchanged)
```

**New child flow** (`twig new child Fix the login page`):
```
Shell:          ["new", "child", "Fix", "the", "login", "page"]
  → CAF route: longest-prefix matches [Command("new child")] → NewChild()
  → CAF parse: title=null (no --title flag), titleParts=["Fix", "the", "login", "page"]
  → JoinTrailingText: named=null, positional=["Fix", "the", "login", "page"] → "Fix the login page"
  → NewCommand.ExecuteChildAsync("Fix the login page", type: null)
    → activeItemResolver.GetActiveItemAsync() → matched ActiveItemResult.Found(parent)
    → processConfig.GetAllowedChildTypes(parent.Type) → ["Issue"]
    → childType = Issue (inferred, single allowed type)
    → Hint: "Inferred type 'Issue' from parent. Use --type to override."
    → SeedFactory.CreateUnparented("Fix the login page", Issue, parent.AreaPath, ...)
    → adoService.CreateAsync(seed) → 1529
    → adoService.FetchAsync(1529) → fetched
    → workItemRepo.SaveAsync(fetched)
    → "Created #1529 Fix the login page (Issue)"
```

**Smart type inference flow** (`twig new "My Task" --parent 1527`):

> **Apparent concern**: With `--parent` interleaved, trailing text could include "Task" from a title like "My Task". **Actual behavior**: ConsoleAppFramework parses named params by name (`--parent 1527` extracts as a pair), so remaining positional args are correctly captured. The actual parse is: title=null, titleParts=["My", "Task"], parent=1527 → effectiveTitle="My Task". This works correctly because named flags are consumed before positional arg collection.

```
Shell:          ["new", "My", "Task", "--parent", "1527"]
  → CAF parse: title=null, titleParts=["My", "Task"], parent=1527, type=null
  → JoinTrailingText: named=null, positional=["My", "Task"] → "My Task"
  → NewCommand.ExecuteAsync("My Task", type: null, parent: 1527)
    → parent is provided, type is null → enter inference path
    → activeItemResolver.ResolveByIdAsync(1527) → ActiveItemResult.Found(parent) (Issue)
    → processConfig.GetAllowedChildTypes("Issue") → ["Task"]
    → childType = Task (inferred)
    → Hint: "Inferred type 'Task' from parent. Use --type to override."
    → SeedFactory.CreateUnparented("My Task", Task, parentArea, parentIter, assignedTo, 1527)
    → adoService.CreateAsync(seed) → 1530
    → "Created #1530 My Task (Task)"
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Join trailing text in `Program.cs`, not in command classes | Keeps command classes testable with simple string parameters; `Program.cs` is the natural arg-parsing boundary. Tests can pass explicit strings without needing to simulate trailing text arrays. |
| DD-2 | `twig commit` excluded from trailing text | `params string[] passthrough` serves dual purpose (message continuation + git flags). Joining would break `twig commit "msg" --amend`. Users already use quotes for commit messages naturally. |
| DD-3 | `new child`/`new sibling` as sub-commands, not flags | Follows existing `seed new`, `link parent`, `nav up` patterns. More discoverable than `--relationship child` flag. Allows distinct help text per relationship type. |
| DD-4 | Type inference uses first allowed child type when multiple are available; emits discoverability hint | Matches `SeedFactory.Create` precedent (line 62). Deterministic and process-agnostic. Users can override with `--type`. The hint `"Inferred type '{type}' from parent. Use --type to override."` surfaces the inference and teaches the override. |
| DD-5 | `ActiveItemResolver` and `IProcessConfigurationProvider` are required constructor params on `NewCommand` | Both services are already registered in DI; auto-wiring resolves them without a factory lambda. Code paths that don't use them (e.g., plain `twig new --type Task`) simply don't call them — no null checks needed. Adding them as required params is consistent with the existing DI pattern for `NoteCommand` (which requires `ActiveItemResolver`) and `SeedNewCommand` (which requires both). |
| DD-6 | `new child`/`sibling` publish immediately (no seed workflow) | These are "I know what I want" shortcuts. Seed workflow remains for draft-oriented creation with review/validation steps. |
| DD-7 | `new sibling` uses active item's type as default (not parent's first child type), validated against parent's allowed children | More intuitive: "create another one like this" rather than "create the default child of my parent." Validation against `GetAllowedChildTypes(parent.Type)` ensures the ADO API won't reject the request with an opaque error. |
| DD-8 | Child/sibling flows use `SeedFactory.CreateUnparented` with explicit type resolution in the CLI layer, rather than delegating to `SeedFactory.Create` | `SeedFactory.Create` handles type inference and validation internally (~20 LoC savings), but the CLI layer needs to emit discoverability hints (`"Inferred type '{type}' from parent. Use --type to override."`) between the type resolution and seed creation steps. With `Create`, the inference is opaque — the caller cannot distinguish inferred from explicit types. The current approach keeps the hint emission point visible. If future refactoring extracts a `TypeResolutionResult` from `SeedFactory`, this decision could be revisited. |

## Alternatives Considered

### Trailing Text Implementation Approach

Four approaches were evaluated for enabling `twig note starting work` without quotes:

**Option A: `[Argument]` + `params string[]`** (R2 design — **rejected**)

```csharp
public async Task<int> Note([Argument] string? text = null, ..., params string[] textParts)
```

- ✅ First positional word goes into `text`, rest into `textParts`; natural join
- ❌ **Breaks backward compatibility**: `[Argument]` converts `text` from a named parameter to a positional-only parameter. `--text "value"` no longer works — ConsoleAppFramework does not support dual positional+named access. Violates FR-02, FR-05, and G-7.
- ❌ `--title` would similarly break for `twig new`

**Option B: `params string[]` only (no `[Argument]`)** — **selected**

```csharp
public async Task<int> Note(string? text = null, ..., params string[] textParts)
```

- ✅ `--text "value"` continues to work (named parameter unchanged)
- ✅ Trailing positional words captured by `params` when no `--text` is given
- ✅ Clean precedence: named param wins; positional args are fallback
- ✅ No framework-level behavior change to existing parameters
- ⚠ Slightly less obvious that positional args are supported (mitigated by help text)

**Option C: Custom ConsoleAppFramework filter/middleware**

- ✅ Could intercept raw args and pre-join before dispatch
- ❌ Requires intimate knowledge of CAF internals; fragile across framework upgrades
- ❌ No existing middleware pattern in the codebase; would be the first
- ❌ Harder to test in isolation

**Option D: Pre-processing hook in `Program.cs` (before `app.Run`)**

- ✅ Could inspect and rewrite `args[]` before ConsoleAppFramework parses
- ❌ Would need command-specific knowledge (which commands support trailing text)
- ❌ Duplicates CAF's own argument parsing logic
- ❌ Cannot distinguish `twig note --text "value" extra` from `twig note Hello world` at the raw args level without re-implementing named-param detection

**Decision**: Option B was selected because it preserves full backward compatibility (the critical issue identified in R2 review), requires no framework internals knowledge, and follows the established `params string[]` pattern already used by `Commit` and `SeedChain`.

## Dependencies

### External Dependencies
None — all changes use existing ConsoleAppFramework capabilities, Spectre.Console, and ADO REST APIs.

### Internal Dependencies
- `SeedFactory.CreateUnparented` — reused for child/sibling creation (no changes needed)
- `ActiveItemResolver` — already exists (used by `NoteCommand`, `SeedNewCommand`, etc.), added as required constructor dependency to `NewCommand`
- `IProcessConfigurationProvider` — already exists (used by `SeedNewCommand`, `BranchCommand`, etc.), added as required constructor dependency to `NewCommand`
- `ProcessConfiguration.GetAllowedChildTypes` — already exists, used for type inference and validation

### Sequencing Constraints
- Issue #1527 (trailing text) should merge before #1528 (smart defaults) since `new child`/`new sibling` will use the trailing text pattern for title arguments
- Within #1528, smart type inference (T-1528-1) should land before sub-commands (T-1528-2/3) since the sub-commands depend on the inference logic

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ConsoleAppFramework `params` interleaving with named flags produces unexpected parse results | Low | Medium | Comprehensive test matrix covering: (a) all flag orderings before/after positional args, (b) `--type` and `--parent` interleaved with trailing title words, (c) `--output json` at start/middle/end of args. See T-1527-2 test cases for the specific matrix. |
| Sub-command/positional title ambiguity: titles starting with "child" or "sibling" are consumed as sub-command tokens | Medium | Low | Documented in architecture overview. Users can always use `--title` to bypass sub-command routing. This is inherent to ConsoleAppFramework's longest-prefix matching and consistent with `seed`/`seed new` behavior. |
| `twig new` backward compatibility: existing scripts using `--title "X"` | Low | Low | Risk eliminated by the params-only approach: `title` remains a regular named parameter, so `--title "X"` continues to work. No `[Argument]` is added. Only the `type` parameter changes from required to optional (`string?`), which is backward-compatible since existing `--type Task` invocations still work. |
| Type inference selects wrong type when multiple child types are valid | Low | Low | Uses first-allowed-child (same as `seed new`); `--type` override always available; discoverability hint surfaces the inference |
| `ActiveItemResolver.GetActiveItemAsync()` returns `NoContext` or `Unreachable` in child/sibling flows | Medium | Medium | All four `ActiveItemResult` variants are explicitly handled via `TryGetWorkItem` extension (matching the established pattern in 17+ existing call sites). `NoContext` → error with `twig set` guidance. `Unreachable(id, reason)` → error with the ADO reason string. See child flow step 2 and sibling flow step 2. |
| Help text changes: adding new sub-commands alters ConsoleAppFramework's auto-generated usage strings | Medium | Low | Add a manual verification step (T-1527-2 / T-1528-4) to inspect help output for `twig new --help` and `twig new child --help`, ensuring usage strings are clear and discoverable. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| _(none)_ | All changes are to existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Add `params string[]` to `Note()` and `New()` methods (no `[Argument]` — named params preserved); add `JoinTrailingText` helper; add `[Command("new child")]` and `[Command("new sibling")]` methods; make `type` param optional on `New()` |
| `src/Twig/Commands/NewCommand.cs` | Make `type` parameter nullable; add type inference from parent via `ProcessConfiguration`; add `ExecuteChildAsync` and `ExecuteSiblingAsync` methods with explicit `ActiveItemResult` error handling, type validation, and discoverability hints; add `ActiveItemResolver` and `IProcessConfigurationProvider` as required constructor params |
| `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | Add tests for positional text argument and trailing text joining |
| `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | Update all 18 existing test methods to pass new constructor params (mechanical); add tests for optional type with inference, `ExecuteChildAsync`, `ExecuteSiblingAsync`, trailing title text, type validation, error messages, help text verification |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| _(none)_ | No files are deleted in this change |

## ADO Work Item Structure

### Epic #1526: Command UX & Ergonomics _(existing)_

---

### Issue #1527: Evaluate treating unquoted trailing text as a single argument for key commands _(existing)_

**Goal**: Allow users to type `twig note starting work` instead of `twig note --text "starting work"`, and `twig new Fix the bug --type Task` instead of `twig new --title "Fix the bug" --type Task`.

**Prerequisites**: None

**Tasks**:

#### T-1527-1: Add trailing text support to `twig note` and `twig new`
- Add `JoinTrailingText(string? named, string[]? positional)` private helper to `TwigCommands` in `Program.cs`
- Add `params string[] textParts` to `Note()` — `text` remains an undecorated named parameter (no `[Argument]`), preserving `--text` access **(FR-01, FR-02, G-7)**
  - Named param takes precedence: `text ?? (textParts join)` **(FR-03 — editor flow when both null)**
- Add `params string[] titleParts` to `New()` — `title` remains an undecorated named parameter, preserving `--title` access **(FR-04, FR-05, G-7)**
  - Make `title` optional (`string? title = null`) and `type` optional (`string? type = null`)
  - When both `title` and `type` are null and no `--parent`, error: `"Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."` **(FR-10)**
- **Files**: `src/Twig/Program.cs`
- **Effort**: ~60 LoC

#### T-1527-2: Unit tests for trailing text behavior
- **Note tests** **(FR-01, FR-02, FR-03)**:
  - Positional text: `textParts=["Hello", "world"]` → effective text "Hello world"
  - Multi-word text (3+ words): `textParts=["starting", "work", "on", "feature"]`
  - Mixed with `--output json` flag (before/after positional text)
  - Empty text (editor flow): `text=null, textParts=[]` → null (triggers editor)
  - Backward compat: `text="my note text", textParts=[]` → "my note text"
  - **Conflict resolution**: `text="explicit text", textParts=["trailing", "words"]` → effective text is `"explicit text"` (named `--text` param wins; trailing positional text is silently ignored per `JoinTrailingText` precedence rule)
- **New tests** **(FR-04, FR-05)**:
  - Positional title: `titleParts=["Fix", "the", "bug"]`
  - Multi-word title with `--type` flag (before/after positional)
  - Mixed with `--parent` flag interleaved
  - **Conflict resolution**: `title="explicit title", titleParts=["trailing", "text"]` → effective title is `"explicit title"` (named param wins; trailing text is silently ignored per `JoinTrailingText` precedence rule)
- **Params interleaving matrix**:
  - `twig new Fix the bug --type Task`
  - `twig new --type Task Fix the bug`
  - `twig new Fix the bug --parent 123 --type Task`
- **Files**: `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs`
- **Effort**: ~140 LoC


**Acceptance Criteria**:
- [ ] `twig note starting work on feature` produces the same result as `twig note --text "starting work on feature"`
- [ ] `twig note` with no args opens the editor (no regression)
- [ ] `twig new Fix the login page --type Task` produces the same result as `twig new --title "Fix the login page" --type Task`
- [ ] `twig commit "my message" --amend` continues to work (passthrough preserved)
- [ ] All existing tests pass without modification

---

### Issue #1528: Smart defaults and relationship sub-commands for twig new _(existing)_

**Goal**: Make `twig new` context-aware with type inference, and add `twig new child` and `twig new sibling` sub-commands for direct-to-ADO creation with parent context.

**Prerequisites**: Issue #1527 (trailing text support provides the positional title pattern used by sub-commands)

**Tasks**:

#### T-1528-1: Smart type inference for `twig new`
- Make `--type` optional on `NewCommand.ExecuteAsync` (`string? type = null`) **(FR-10, FR-11)**
- When `--parent` is provided and `--type` is omitted:
  - Resolve parent via `ActiveItemResolver.ResolveByIdAsync(parentId)` — handle all `ActiveItemResult` variants via `TryGetWorkItem`
  - Query `ProcessConfiguration.GetAllowedChildTypes(parent.Type)` (returns `IReadOnlyList<WorkItemType>` — already canonical values)
  - Use first allowed child type
- When `--type` IS provided: parse via `WorkItemType.Parse(type)`, validate against allowed children if `--parent` given
- Both paths converge at a `WorkItemType childType` local before calling `SeedFactory.CreateUnparented`
- Emit discoverability hint when type is inferred: `"Inferred type '{type}' from parent. Use --type to override."`
- When no `--type` and no `--parent`, error: `"Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."` **(FR-10)**
- Add `ActiveItemResolver` and `IProcessConfigurationProvider` as required constructor params (auto-wired by DI)
- Update all 18 existing `NewCommandTests` to pass the 2 new constructor args (mechanical — `Substitute.For<>()`)
- **Files**: `src/Twig/Commands/NewCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs`
- **Effort**: ~80 LoC

#### T-1528-2: Add `twig new child` sub-command
- Implement `ExecuteChildAsync` on `NewCommand` **(FR-06, FR-07, FR-09, FR-12, FR-13, FR-14)**:
  - Validate title is non-empty, else error: `"Title is required. Usage: twig new child \"title\""` **(FR-13)**
  - Resolve active context via `ActiveItemResolver.GetActiveItemAsync()` — pattern-match `ActiveItemResult` via `TryGetWorkItem`:
    - `NoContext` → error: `"No active work item. Run 'twig set <id>' first."` (exit 1)
    - `Unreachable(id, reason)` → error: `"Active item #{id} is unreachable: {reason}"` (exit 1)
  - Infer type from `ProcessConfiguration.GetAllowedChildTypes(parent.Type)` **(FR-06)**
  - If `--type` provided, validate against allowed children **(FR-07, FR-14)**
  - Inherit area/iteration from parent **(FR-09)**
  - Create via `adoService.CreateAsync(seed)`, fetch back, save to cache
  - If `--set`, set active context **(FR-12)**
- Wire `[Command("new child")]` in `Program.cs` with `title?`, `--type`, `--set`, `params titleParts`
- **Files**: `src/Twig/Commands/NewCommand.cs`, `src/Twig/Program.cs`
- **Effort**: ~95 LoC

#### T-1528-3: Add `twig new sibling` sub-command
- Implement `ExecuteSiblingAsync` on `NewCommand` **(FR-08, FR-09, FR-12, FR-13, FR-14)**:
  - Validate title is non-empty, else error: `"Title is required. Usage: twig new sibling \"title\""` **(FR-13)**
  - Resolve active context via `ActiveItemResolver.GetActiveItemAsync()` — same `TryGetWorkItem` pattern as child
  - Validate `active.ParentId` is not null, else error: `"Active item #{id} has no parent — cannot create sibling."` **(FR-08)**
  - Fetch parent via `ActiveItemResolver.ResolveByIdAsync(active.ParentId.Value)` — handle `Unreachable`
  - If `--type` omitted, use active item's type (natural sibling) **(FR-08)**
  - Validate resolved type against `GetAllowedChildTypes(parent.Type)` **(FR-14)**
  - Inherit area/iteration from parent **(FR-09)**
  - Create with `parentId = active.ParentId`
  - If `--set`, set active context **(FR-12)**
- Wire `[Command("new sibling")]` in `Program.cs` with `title?`, `--type`, `--set`, `params titleParts`
- **Files**: `src/Twig/Commands/NewCommand.cs`, `src/Twig/Program.cs`
- **Effort**: ~95 LoC

#### T-1528-4: Tests for smart defaults and sub-commands
- **Type inference tests** **(FR-10, FR-11)**:
  - Type inference from `--parent` when `--type` omitted
  - Error when no `--type` and no `--parent`: `"Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."`
  - Error when parent type has no allowed children: `"Type '{type}' does not allow child items."`
  - Discoverability hint emitted when type inferred
- **Type validation tests** **(FR-14)**:
  - Explicit `--type` rejected when not in allowed children: `"Type '{type}' is not an allowed child of '{parent.Type}'."`
- **New child tests** **(FR-06, FR-07, FR-09, FR-12, FR-13)**:
  - Creates under active context with inferred type
  - Validates explicit type against allowed children
  - Inherits area/iteration paths from parent
  - `--set` flag sets active context
  - `--type` override works
  - Error when `ActiveItemResult.NoContext`: `"No active work item. Run 'twig set <id>' first."`
  - Error when `ActiveItemResult.Unreachable`: `"Active item #{id} is unreachable: {reason}"`
  - Error when title omitted: `"Title is required. Usage: twig new child \"title\""`
- **New sibling tests** **(FR-08, FR-09, FR-14)**:
  - Creates alongside active item with same type
  - Validates type against parent's allowed children
  - Error when active item has no parent: `"Active item #{id} has no parent — cannot create sibling."`
  - Error when title omitted: `"Title is required. Usage: twig new sibling \"title\""`
- **Constructor migration**: all 18 existing test methods already updated in T-1528-1
- **Files**: `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs`
- **Effort**: ~230 LoC

**Acceptance Criteria**:
- [ ] `twig new "My Task" --parent 1527` infers type from parent's process config
- [ ] `twig new "My Task" --parent 1527 --type Bug` uses explicit type override
- [ ] `twig new "My Task"` without `--type` or `--parent` errors: `"Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."`
- [ ] `twig new child "Fix the login page"` creates child of active context in ADO
- [ ] `twig new child` without title errors: `"Title is required. Usage: twig new child \"title\""`
- [ ] `twig new child` inherits area/iteration from parent
- [ ] `twig new child --type Bug` validates Bug is allowed child of parent's type
- [ ] `twig new child --set` sets the new item as active context
- [ ] Type inference emits discoverability hint
- [ ] `twig new sibling "Another task"` creates sibling with same type as active item
- [ ] `twig new sibling --type Bug` validates Bug is allowed child of parent's type
- [ ] `twig new sibling` errors when active item has no parent
- [ ] `twig new child --help` and `twig new sibling --help` produce clear usage text
- [ ] All existing `twig new` invocations continue to work

## PR Groups

PR groups define how the implementation Tasks are clustered into reviewable pull requests. These are a cross-cutting overlay on the ADO hierarchy — not a 1:1 mapping to Issues.

### PR-1: Trailing text support (deep)

**Scope**: All tasks from Issue #1527 (T-1527-1, T-1527-2)

| Attribute | Value |
|-----------|-------|
| **Type** | Deep — few files, complex behavioral changes to arg parsing |
| **Files** | `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` |
| **Estimated LoC** | ~200 |
| **Predecessors** | None — this is the first PR |

**Rationale**: The trailing text pattern is a self-contained behavioral change that affects only the CLI entry points and their tests. Merging this first establishes the `JoinTrailingText` helper and `params string[]` pattern that PR-2 depends on. Reviewers can focus on the arg-parsing semantics and backward compatibility in isolation.

**Review focus**: Backward compatibility — verify `--text` and `--title` named params still work; verify `params` interleaving with named flags.

---

### PR-2: Smart defaults and relationship sub-commands (deep)

**Scope**: All tasks from Issue #1528 (T-1528-1, T-1528-2, T-1528-3, T-1528-4)

| Attribute | Value |
|-----------|-------|
| **Type** | Deep — concentrated changes with complex type inference and error handling |
| **Files** | `src/Twig/Program.cs`, `src/Twig/Commands/NewCommand.cs`, `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` |
| **Estimated LoC** | ~500 |
| **Predecessors** | PR-1 (trailing text pattern must be merged first — sub-commands use `params string[] titleParts`) |

**Rationale**: Type inference, child creation, and sibling creation are tightly coupled — they share the same `NewCommand` class, the same constructor dependencies, and the same type validation logic. Splitting them across PRs would mean merging partial `NewCommand` constructors and re-reviewing the same file multiple times. A single PR keeps the review cohesive.

**Review focus**: Type inference correctness (process-agnostic, no hardcoded types); `ActiveItemResult` error handling (all variants covered); `SeedFactory.CreateUnparented` usage (area/iteration inheritance); `--set` flag behavior.

---

### Execution Order

```
PR-1 (trailing text) ──→ PR-2 (smart defaults + sub-commands)
```

PR-1 must merge before PR-2 because:
1. `NewChild()` and `NewSibling()` in `Program.cs` use `params string[] titleParts` + `JoinTrailingText` — the pattern established in PR-1
2. Constructor migration of `NewCommandTests` (T-1528-1) builds on the test file state from PR-1
3. Merging in order avoids merge conflicts in `Program.cs`

**Total**: 2 PRs, ~700 LoC combined, each ≤500 LoC and ≤5 files — well within reviewability bounds.

## References

- ConsoleAppFramework source-gen argument parsing: positional via `[Argument]`, rest via `params string[]`
- `SeedFactory.Create` (line 21–76 of `src/Twig.Domain/Services/SeedFactory.cs`) — type inference precedent
- `ProcessConfiguration.GetAllowedChildTypes` (line 68–74 of `src/Twig.Domain/Aggregates/ProcessConfiguration.cs`) — child type resolution
- Existing sub-command pattern: `seed new`, `link parent`, `nav up` in `Program.cs`
- Prior plan: `docs/projects/twig-new-command.plan.md` — original `twig new` design

