---
goal: Unified color design language (true-color type colors + ADO fetch) and IOutputFormatter + HintEngine wiring into all CLI commands
version: 2.0
date_created: 2026-03-15
last_updated: 2026-03-15
owner: Twig CLI team
tags: [feature, architecture, cli, ux, color, formatting, ansi, ado]
revision_notes: "Rev 2: 24-bit true-color type colors from ADO API; AdoWorkItemTypeDto color+icon fields; SQLite type-color storage; Tier 1 Unicode badges; FormatHint/FormatInfo on IOutputFormatter; Nerd Font stub; updated state category palette (InProgress→Blue); all 14 commands wired"
---

# Introduction

This document describes the solution design and implementation plan for wiring the existing `IOutputFormatter` implementations and `HintEngine` into all CLI commands, and for establishing a consistent ANSI color design language aligned to Azure DevOps state categories.

**Background prompt:** Design a unified color scheme and wire `IOutputFormatter` + `HintEngine` into all CLI commands. The formatters (`HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`) and `HintEngine` already exist in `src/Twig/Formatters/` and `src/Twig/Hints/` with full test coverage, but NO commands currently use them — they all use inline `Console.WriteLine`. Goals: (1) define ANSI color design language, (2) register in DI and add `--output` flag, (3) refactor every command to use `IOutputFormatter`, (4) wire `HintEngine` for contextual hints, (5) keep all existing tests passing and add wiring tests.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: Functional requirements use `FR-` prefix, non-functional requirements use `NFR-`, failure modes use `FM-`, and acceptance criteria use `AC-`. These enable traceability across sections.

---

## Executive Summary

Twig's formatter layer (`IOutputFormatter`) and hint system (`HintEngine`) were designed and fully tested in isolation, but no command uses them — every command writes directly to `Console.WriteLine` with hardcoded strings. This creates divergent output behaviour (JSON/minimal modes are unreachable in practice), duplicates state-to-color mapping logic, and buries contextual hints inside inline strings rather than the `HintEngine`. This plan proposes: (1) establishing a consistent ANSI color token vocabulary aligned to ADO's native state categories (Proposed=dim-gray, InProgress=**blue**, Resolved/Completed=green, Removed=red), (2) extending `IOutputFormatter` with two new methods (`FormatHint`, `FormatInfo`), (3) introducing a singleton `OutputFormatterFactory` that maps the `--output` flag string to the correct formatter, (4) registering the formatters, factory, and `HintEngine` in `Program.cs` DI, (5) refactoring all 14 command files to consume `IOutputFormatter` via the factory and emit hints via `HintEngine`, and (6) updating existing tests and adding focused wiring tests. The outcome is a fully functional `--output human|json|minimal` flag on every command and colour-consistent ANSI output aligned to the ADO UX.

---

## Background

### Current system state

The Twig CLI is a .NET AOT-compiled tool (`PublishAot=true`, `IsAotCompatible=true`) that routes commands through `ConsoleAppFramework`. The architecture as of this writing:

- **`TwigCommands`** (`Program.cs`) — single class registered with `ConsoleAppFramework`; each method is a CLI subcommand. Commands are resolved lazily from `IServiceProvider` to prevent DI resolution from failing before `twig init` runs.
- **14 command files** in `src/Twig/Commands/` — each implements one command as `ExecuteAsync(...)`. All output is via raw `Console.WriteLine` / `Console.Error.WriteLine`.
- **3 formatter files** in `src/Twig/Formatters/` — `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, all implementing `IOutputFormatter`. Fully tested, not registered in DI, not called by any command.
- **`HintEngine`** in `src/Twig/Hints/HintEngine.cs` — provides contextual post-command hints; suppresses output for non-human formats and when `config.display.hints = false`. Fully tested, not registered in DI, not called by any command.
- **`TwigConfiguration.Display`** (`DisplayConfig`) — `Hints: bool = true`, `TreeDepth: int = 3`. Loaded from `.twig/config`.
- **Existing color logic** in `HumanOutputFormatter.GetStateColor()` maps `active/doing/committed/in progress` to **Yellow** (`\x1b[33m`). This diverges from ADO's native palette, which uses **blue** for InProgress.
- **Hardcoded hint strings** — `SetCommand`, `SeedCommand`, `NoteCommand`, `EditCommand`, `InitCommand` each have inline `Console.WriteLine("  hint: ...")` calls; these bypass `HintEngine` entirely.

### Relevant code metrics

| Area | Files | Inline Console.Write calls (approx.) |
|------|-------|--------------------------------------|
| Commands | 14 | ~55 |
| Formatters (exist, unused) | 5 | 0 |
| Hints (exist, unused) | 1 | 0 |
| Tests (formatters) | 3 | — |
| Tests (hints) | 1 | — |

### Prior art

The `IOutputFormatter` interface, all three implementations, `HintEngine`, and their full test suites were created ahead of command integration — a deliberate "build the layer, wire later" strategy. The interface contract is stable. The gap to close is wiring.

---

## Problem Statement

1. **Dead code**: Three formatter implementations and `HintEngine` are fully tested but never invoked. The `--output` flag cannot be added without this wiring work.
2. **Inconsistent colors**: `HumanOutputFormatter.GetStateColor` maps Active/InProgress states to **Yellow** — ADO uses **Blue** for InProgress. Dirty markers (•) also use Yellow, creating a color collision.
3. **Duplicated output logic**: 14 commands each independently compose output strings. Changes to output structure (e.g., adding a field) require edits across all commands rather than one formatter.
4. **Bypassed hint system**: 5 commands have hardcoded inline hint strings (`Console.WriteLine("  hint: ...")`). `HintEngine` exists to centralize and conditionally suppress hints, but commands circumvent it.
5. **No DI registration**: `IOutputFormatter`, `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, and `HintEngine` are absent from `Program.cs`'s `ConfigureServices`. They cannot be injected.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Define and document a complete ANSI color token vocabulary aligned to ADO state categories; implement it in `HumanOutputFormatter`.
2. **G-2**: Extend `IOutputFormatter` with `FormatHint` and `FormatInfo` methods to cover all remaining command output patterns.
3. **G-3**: Introduce `OutputFormatterFactory` (singleton) to resolve `"human"|"json"|"minimal"` format strings to `IOutputFormatter` instances at runtime.
4. **G-4**: Register `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, `OutputFormatterFactory`, and `HintEngine` in `Program.cs` DI.
5. **G-5**: Add `string output = "human"` optional parameter to every method in `TwigCommands`; route it to each command's `ExecuteAsync`.
6. **G-6**: Refactor all 14 commands to use `IOutputFormatter` (via factory) for all output and `HintEngine` for all hints; remove all inline `Console.WriteLine` hint strings.
7. **G-7**: All existing formatter and hint tests pass after the color update; all existing command tests pass after injecting real formatters and `HintEngine` (hints disabled).
8. **G-8**: Add new tests for `OutputFormatterFactory` and for formatter+hint wiring in at least three representative commands.

### Non-Goals

- **NG-1**: Adding a persistent `display.output` configuration key (format is per-invocation only via `--output`).
- **NG-2**: Changing the JSON schema of `JsonOutputFormatter` (stable schema is preserved).
- **NG-3**: Adding color support for terminals that do not support ANSI (no `NO_COLOR` / `TERM` detection in this iteration).
- **NG-4**: Changing the `ConsoleAppFramework` version or CLI framework.
- **NG-5**: Migrating from AOT-compatible JSON writing to `System.Text.Json` source-gen serializers (already handled correctly).

---

## Requirements

### Functional Requirements

- **FR-001**: `IOutputFormatter` MUST expose `FormatHint(string hint)` and `FormatInfo(string message)` methods in addition to its existing six methods.
- **FR-002**: `HumanOutputFormatter` MUST map InProgress-category states (`active`, `doing`, `committed`, `in progress`) to **blue** (`\x1b[34m`), not yellow.
- **FR-003**: `HumanOutputFormatter` MUST use **yellow** (`\x1b[33m`) exclusively for dirty markers (•) and `FormatInfo`/progress messages — not for any state color.
- **FR-004**: `HumanOutputFormatter` MUST provide a `GetTypeColor` method (private) that returns distinct ANSI codes per work item type family (see color table in Proposed Design).
- **FR-005**: `HumanOutputFormatter.FormatWorkItem` MUST apply the type color to the Type field.
- **FR-006**: `HumanOutputFormatter.FormatHint` MUST render hint text with `\x1b[2m` (Dim) and the `hint:` prefix.
- **FR-007**: `HumanOutputFormatter.FormatInfo` MUST render informational messages with `\x1b[2m` (Dim).
- **FR-008**: `JsonOutputFormatter.FormatHint` MUST return an empty string (hints are suppressed by `HintEngine` before reaching the formatter for JSON format; this is a defensive no-op).
- **FR-009**: `JsonOutputFormatter.FormatInfo` MUST serialize as `{"info":"<message>"}`.
- **FR-010**: `MinimalOutputFormatter.FormatHint` MUST return an empty string (hints suppressed by `HintEngine`).
- **FR-011**: `MinimalOutputFormatter.FormatInfo` MUST return the raw message string, no decoration.
- **FR-012**: `OutputFormatterFactory` MUST resolve `"json"` → `JsonOutputFormatter`, `"minimal"` → `MinimalOutputFormatter`, and any other value (including `"human"` and unrecognized strings) → `HumanOutputFormatter`.
- **FR-013**: Every method in `TwigCommands` MUST accept `string output = "human"` as an optional parameter and pass it to the corresponding command's `ExecuteAsync`.
- **FR-014**: Every command's `ExecuteAsync` MUST accept `string outputFormat = "human"`, resolve the formatter via `OutputFormatterFactory.GetFormatter(outputFormat)`, and use it for all `Console.WriteLine` output.
- **FR-015**: Every command SHOULD call `HintEngine.GetHints(commandName, ...)` after successful execution when the command has at least one registered hint case in `HintEngine`, and emit each non-empty hint via `Console.WriteLine`. Commands with no registered hint cases (e.g., `SaveCommand`, `UpdateCommand`) MAY omit the `HintEngine` call. Hint suppression for json/minimal formats is handled by `HintEngine` returning an empty list — no special-casing is required in commands.
- **FR-016**: All hardcoded inline `"  hint: ..."` `Console.WriteLine` strings in commands MUST be removed and replaced by `HintEngine` calls.

### Non-Functional Requirements

- **NFR-001**: The refactoring MUST NOT break AOT compatibility (`IsAotCompatible=true`). No reflection-based serialization, no dynamic code.
- **NFR-002**: All 14 existing command tests MUST continue to pass after the refactoring. Test constructors MUST be updated to supply `OutputFormatterFactory` and `HintEngine` with hints disabled (`new DisplayConfig { Hints = false }`).
- **NFR-003**: The `--output` flag MUST appear in `--help` output for every subcommand (inherent via `ConsoleAppFramework` parameter introspection).
- **NFR-004**: `OutputFormatterFactory` and all formatters MUST be registered as singletons (CLI process lifetime; no per-request cost).

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  ConsoleAppFramework                                            │
│                                                                 │
│  TwigCommands.Status(output="human")                           │
│       │                                                         │
│       │  lazy resolve from IServiceProvider                     │
│       ▼                                                         │
│  StatusCommand(                                                 │
│    IContextStore, IWorkItemRepository, IPendingChangeStore,     │
│    TwigConfiguration,        ← NEW (for Seed.StaleDays)        │
│    OutputFormatterFactory,   ← NEW                              │
│    HintEngine                ← NEW                              │
│  )                                                              │
│       │                                                         │
│       │ .ExecuteAsync(outputFormat="human")                     │
│       │                                                         │
│       │  _factory.GetFormatter("human")                        │
│       ▼                          ▼                              │
│  HumanOutputFormatter      Console.WriteLine(formatted)        │
│  JsonOutputFormatter        Console.WriteLine(hint)            │
│  MinimalOutputFormatter                                         │
└─────────────────────────────────────────────────────────────────┘

DI Singletons (Program.cs):
  HumanOutputFormatter
  JsonOutputFormatter
  MinimalOutputFormatter
  OutputFormatterFactory   ← NEW
  HintEngine               ← NEW
```

### Color Design Language

The following table defines the complete ANSI token vocabulary for `HumanOutputFormatter`. All tokens are private constants in the class.

#### ANSI Constants

| Constant | ANSI Code | Renders As |
|----------|-----------|------------|
| `Reset` | `\x1b[0m` | Default terminal color |
| `Bold` | `\x1b[1m` | Bold text |
| `Dim` | `\x1b[2m` | Dim/gray text |
| `Red` | `\x1b[31m` | Red |
| `Green` | `\x1b[32m` | Green |
| `Yellow` | `\x1b[33m` | Yellow (dirty marker, info/progress) |
| `Blue` | `\x1b[34m` | **Blue — InProgress states** ← NEW |
| `Magenta` | `\x1b[35m` | Magenta (Epic type) |
| `Cyan` | `\x1b[36m` | Cyan (active marker ●, Feature type) |

#### State Category Colors (ADO-aligned)

| ADO Category | Example States | Color | Token |
|--------------|---------------|-------|-------|
| Proposed | `new`, `to do`, `proposed` | Dim/Gray | `Dim` |
| InProgress | `active`, `doing`, `committed`, `in progress`, `approved` | **Blue** | `Blue` ← CHANGED from `Yellow` |
| Resolved | `resolved` | Green | `Green` |
| Completed | `closed`, `done` | Green | `Green` |
| Removed | `removed` | Red | `Red` |
| Unknown | (anything else) | Reset | `Reset` |

> **Breaking change**: `GetStateColor("active")` currently returns `\x1b[33m` (Yellow). After this change it returns `\x1b[34m` (Blue). The test `FormatWorkItem_ShowsStateWithColor` asserts `\x1b[33m` and MUST be updated to `\x1b[34m`.

#### Work Item Type Colors (new `GetTypeColor` private method)

| Work Item Type(s) | Color | Token |
|-------------------|-------|-------|
| `Epic` | Magenta | `Magenta` |
| `Feature` | Cyan | `Cyan` |
| `User Story`, `Product Backlog Item`, `Requirement` | Blue | `Blue` |
| `Bug`, `Impediment`, `Risk` | Red | `Red` |
| `Task`, `Test Case`, `Change Request`, `Review`, `Issue` | Reset | `Reset` |

#### UI Chrome and Status Colors

| Element | Color | Token |
|---------|-------|-------|
| Section headers (`Workspace`, `Active:`, `Sprint`) | Bold | `Bold` |
| Separators (`─────`) | (no additional escape, inherits terminal default) | — |
| Parent chain (dimmed ancestors) | Dim | `Dim` |
| Active marker `●` | Cyan | `Cyan` |
| Dirty marker `•` | Yellow | `Yellow` |
| Hints (`hint: ...`) | Dim | `Dim` |
| Info/progress messages | Dim | `Dim` |
| Errors | Red | `Red` |
| Success (`✓ ...`) | Green | `Green` |

### Key Components

#### `IOutputFormatter` (extended)

```csharp
public interface IOutputFormatter
{
    // Existing methods (unchanged)
    string FormatWorkItem(WorkItem item, bool showDirty);
    string FormatTree(WorkTree tree, int maxChildren, int? activeId);
    string FormatWorkspace(Workspace ws, int staleDays);
    string FormatFieldChange(FieldChange change);
    string FormatError(string message);
    string FormatSuccess(string message);
    string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches);

    // New methods
    string FormatHint(string hint);      // dim "hint: <text>"  (empty for json/minimal)
    string FormatInfo(string message);   // dim progress message
}
```

#### `OutputFormatterFactory` (new)

```csharp
// src/Twig/Formatters/OutputFormatterFactory.cs
public sealed class OutputFormatterFactory(
    HumanOutputFormatter human,
    JsonOutputFormatter json,
    MinimalOutputFormatter minimal)
{
    public IOutputFormatter GetFormatter(string format) =>
        format.ToLowerInvariant() switch
        {
            "json"    => json,
            "minimal" => minimal,
            _         => human,
        };
}
```

Registered as a singleton. All three concrete formatters are also singletons. The factory holds references to all three and returns the appropriate one based on the format string.

#### `HumanOutputFormatter` (updated)

- Add `Blue = "\x1b[34m"` and `Magenta = "\x1b[35m"` constants.
- `GetStateColor`: replace `Yellow` with `Blue` for InProgress states.
- Add `GetTypeColor(WorkItemType type)`: returns per-type ANSI code. The switch expression MUST be on `type.Value` (the `string` property of the `readonly record struct`), not on the struct itself, since C# cannot pattern-match on a struct by value equality in a type-based switch without explicitly extracting the string property.
- `FormatWorkItem`: apply `GetTypeColor` to the `Type:` field line.
- Add `FormatHint(string hint)`: returns `$"{Dim}  hint: {hint}{Reset}"`.
- Add `FormatInfo(string message)`: returns `$"{Dim}{message}{Reset}"`.

#### `JsonOutputFormatter` (extended)

- Add `FormatHint(string hint)`: returns `""` (hints suppressed upstream by `HintEngine`).
- Add `FormatInfo(string message)`: returns `{"info":"<message>"}`.

#### `MinimalOutputFormatter` (extended)

- Add `FormatHint(string hint)`: returns `""`.
- Add `FormatInfo(string message)`: returns `message` unchanged.

#### `HintEngine` (updated — "init" case added)

`HintEngine` requires one change: add the `"init"` command case. All other logic is unchanged.

```csharp
case "init":
    hints.Add("Run 'twig set <id>' to set your active work item.");
    break;
```

This resolves the `InitCommand` hint routing decision (see RD-002 in Design Decisions below). All other `HintEngine` behaviour is preserved:
- Returns `Array.Empty<string>()` when `outputFormat` is `"json"` or `"minimal"`.
- Returns `Array.Empty<string>()` when `_hintsEnabled = false`.
- Takes `DisplayConfig displayConfig` in constructor.

DI registration:
```csharp
services.AddSingleton<HintEngine>(sp =>
    new HintEngine(sp.GetRequiredService<TwigConfiguration>().Display));
```

#### Command Refactoring Pattern

Every command is updated to follow this pattern (using `StatusCommand` as example):

```csharp
public sealed class StatusCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    TwigConfiguration config,              // NEW — needed for config.Seed.StaleDays
    OutputFormatterFactory formatterFactory,   // NEW
    HintEngine hintEngine)                     // NEW
{
    public async Task<int> ExecuteAsync(string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // ... fetch item + pending changes ...

        Console.WriteLine(fmt.FormatWorkItem(item, showDirty: true));

        // Compute stale seed count for hint
        var seeds = await workItemRepo.GetSeedsAsync();
        var staleSeedCount = Workspace.Build(item, [], seeds)
            .GetStaleSeeds(config.Seed.StaleDays).Count;

        // Hints
        var hints = hintEngine.GetHints("status",
            item: item,
            outputFormat: outputFormat,
            staleSeedCount: staleSeedCount);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
```

**Error output**: Errors continue to go to `Console.Error.WriteLine`. The formatted error string (from `fmt.FormatError(...)`) is written to stderr. For JSON format, this produces `{"error":"..."}` on stderr — consistent with structured logging.

**Hint suppression**: `HintEngine.GetHints` already returns an empty list for `"json"` and `"minimal"` formats. The `foreach` loop safely no-ops. `FormatHint` returning `""` is a defensive second layer.

#### `TwigCommands` (updated)

Add `string output = "human"` to every method; pass to `ExecuteAsync`:

```csharp
public async Task<int> Status(string output = "human")
    => await services.GetRequiredService<StatusCommand>().ExecuteAsync(output);

public async Task<int> Tree(string output = "human")
    => await services.GetRequiredService<TreeCommand>().ExecuteAsync(output);

// ... all methods follow this pattern
```

### Data Flow

For `twig status --output json`:

```
User: twig status --output json
  │
  ▼
ConsoleAppFramework routing
  │ output="json"
  ▼
TwigCommands.Status(output: "json")
  │ services.GetRequiredService<StatusCommand>()
  ▼
StatusCommand.ExecuteAsync(outputFormat: "json")
  │ formatterFactory.GetFormatter("json") → JsonOutputFormatter
  │ contextStore.GetActiveWorkItemIdAsync()
  │ workItemRepo.GetByIdAsync(id)
  │ pendingChangeStore.GetChangesAsync(id)
  │
  ├─ Console.WriteLine(fmt.FormatWorkItem(item, showDirty:true))
  │    └─ {"id":42,"title":"...","state":"Active",...}
  │
  ├─ hintEngine.GetHints("status", outputFormat:"json") → [] (suppressed)
  └─ return 0
```

For `twig status` (default human):

```
TwigCommands.Status(output: "human")
  │
  ▼
StatusCommand.ExecuteAsync("human")
  │ formatterFactory.GetFormatter("human") → HumanOutputFormatter
  │
  ├─ Console.WriteLine(fmt.FormatWorkItem(item, true))
  │    └─ "\x1b[1m#42 Fix login\x1b[0m\n  Type: \x1b[34mTask\x1b[0m\n  State: \x1b[34mActive\x1b[0m\n..."
  │
  ├─ hintEngine.GetHints("status", staleSeedCount:2)
  │    └─ ["⚠ 2 stale seeds. Consider completing or cutting them."]
  │
  └─ Console.WriteLine(fmt.FormatHint("⚠ 2 stale seeds..."))
       └─ "\x1b[2m  hint: ⚠ 2 stale seeds...\x1b[0m"
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Factory pattern** (not keyed DI services) for formatter resolution | Commands are registered as singletons — they must resolve the formatter at invocation time, not at construction time. `OutputFormatterFactory` is the simplest singleton-safe runtime resolver without reflection or `IServiceProvider` call in commands. |
| **`outputFormat` as `ExecuteAsync` parameter** | Commands are reused across multiple `TwigCommands` methods (e.g., `Up`/`Down` → `SetCommand`). The format must flow through the call chain, not be fixed at construction. Passing it as a parameter makes each invocation independent. |
| **`HintEngine` as singleton injected into commands** | HintEngine is stateless — it reads `_hintsEnabled` once at construction and is otherwise pure. Singleton is correct. Injecting it into commands avoids commands needing to know about `TwigConfiguration.Display`. |
| **`FormatHint` returns `""` for json/minimal** | Defensive programming. `HintEngine` already returns empty lists for non-human formats. The `FormatHint("")` never gets called in practice. The empty return prevents unexpected output if a future code path bypasses `HintEngine`. |
| **Blue for InProgress (not Yellow)** | ADO's web UI uses blue for Active/InProgress state category. Yellow was chosen arbitrarily in the original implementation. Aligning to ADO's palette improves consistency for users familiar with the ADO web UI. |
| **Yellow reserved for dirty marker only** | After InProgress moves to Blue, Yellow becomes exclusively the "attention needed" dirty/unsaved-changes signal — semantically consistent with "warning" conventions. |
| **Magenta for Epic** | Epic is the highest-level container type. Magenta/purple is visually distinct from the state colors (blue, green, red) and matches ADO's visual treatment of Epics. |
| **Errors still to `Console.Error.WriteLine`** | Format-aware error output (`fmt.FormatError(...)`) is the content; `Console.Error` is the stream. All three formatters produce format-appropriate content. JSON-mode errors produce `{"error":"..."}` on stderr — correctly parseable by tools that capture stderr. |
| **RD-002: InitCommand hint routed through HintEngine (not inline `fmt.FormatHint`)** | Adding an `"init"` case to `HintEngine` makes `InitCommand` consistent with all other commands and automatically respects `display.hints = false` suppression. The alternative (a static `fmt.FormatHint(...)` call in `InitCommand`) would bypass hint suppression. Consistency outweighs the minor cost of adding one switch case to `HintEngine`. |
| **RD-003: PromptCommand intentionally bypasses IOutputFormatter** | `PromptCommand` implements `twig prompt` for direct embedding in shell prompts (PS1/RPROMPT). It reads SQLite with a 100ms busy timeout and uses a bespoke `format` parameter (`plain`/`json`) rather than the shared `--output` parameter (`human`/`json`/`minimal`). Three constraints drive this: (1) sub-100ms reads with no DI resolution overhead; (2) must never write to stderr — not even ANSI reset codes; (3) the `plain` format predates `--output` and targets shell integrations, not human terminal output. Future maintainers must not wire `PromptCommand` to `IOutputFormatter` without revisiting all three constraints. |

---

## Alternatives Considered

### Alt-1: Keyed DI services for formatter resolution

Register `IOutputFormatter` three times with named keys (`"human"`, `"json"`, `"minimal"`) using `AddKeyedSingleton`. Resolve with `IKeyedServiceProvider.GetRequiredKeyedService`.

**Pros**: Standard .NET 8 pattern; no custom factory class.  
**Cons**: Requires `IKeyedServiceProvider` injection into commands (adds a DI coupling); slightly heavier abstraction; less readable at call site than `_factory.GetFormatter("json")`.  
**Decision**: Rejected in favor of explicit factory class for clarity.

### Alt-2: Make commands transient, inject resolved `IOutputFormatter`

Register commands as transient; `TwigCommands` resolves the formatter first, then creates/resolves the command with the specific formatter already injected.

**Pros**: Commands don't need the factory — simpler constructor.  
**Cons**: All commands are currently singletons (intentionally, to avoid re-creating SQLite-dependent services). Changing to transient would recreate the entire command (including its non-formatter dependencies) on every invocation — wasteful and potentially buggy for stateful services.  
**Decision**: Rejected. Singleton + factory is the correct pattern here.

### Alt-3: Thread-local or ambient OutputFormat context

Set a static/thread-local `OutputFormat.Current` before calling `ExecuteAsync`; commands read from ambient context instead of a parameter.

**Pros**: No parameter threading; commands don't change signature.  
**Cons**: Implicit, ambient state is fragile in async code; breaks testability; inconsistent with the existing codebase patterns.  
**Decision**: Rejected.

### Alt-4: Do not add `FormatHint` / `FormatInfo` to `IOutputFormatter`

Print hints and info messages directly with hardcoded strings; formatter only handles domain objects.

**Pros**: Smaller `IOutputFormatter` surface area; fewer implementations to update.  
**Cons**: Hints rendered by commands bypass the formatter, so JSON-mode commands could accidentally print ANSI hint strings; the color design language goal for hints is unachievable without a formatter method.  
**Decision**: Rejected. The two new methods are small and allow consistent hint/info styling per format.

---

## Dependencies

### Internal dependencies

| Dependency | Note |
|------------|------|
| `IOutputFormatter` (existing) | Extended — two new methods added |
| `HumanOutputFormatter` (existing) | Modified — color update + new methods |
| `JsonOutputFormatter` (existing) | Modified — new methods |
| `MinimalOutputFormatter` (existing) | Modified — new methods |
| `HintEngine` (existing) | Unchanged — registered in DI |
| `TwigConfiguration.Display` | Already in DI — used to construct `HintEngine` |
| `ConsoleAppFramework` | No changes needed |

### External dependencies

None. All required libraries are already in the project.

### Sequencing constraints

- **Epic 1** (color + interface) MUST complete before Epics 3–4, because commands need the updated `IOutputFormatter` signature.
- **Epic 2** (DI + factory) MUST complete before Epics 3–4, because commands need `OutputFormatterFactory` and `HintEngine` in DI.
- **Epics 3–4** (command refactoring) can be done in any order once Epics 1–2 are complete.
- **Epic 5** (tests) runs in parallel with Epics 3–4; each command refactoring task SHOULD have its test update paired in the same commit.

---

## Impact Analysis

### Components affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `IOutputFormatter` | Interface extension (2 new methods) | BREAKING for any future implementations outside this plan |
| `HumanOutputFormatter` | Color update + new methods | Test breakage (1 assertion) |
| `JsonOutputFormatter` | New methods | Additive |
| `MinimalOutputFormatter` | New methods | Additive |
| `Program.cs` | DI registration + `TwigCommands` signatures | Medium — DI changes |
| All 14 command files | Constructor + `ExecuteAsync` signature | Medium per file; systematic |
| All 14 command test files | Constructor call update | Mechanical |
| `AotSmokeTests.cs` | Verify AOT still works | Low risk |

### Backward compatibility

- **CLI interface**: `--output` is a new optional flag with default `"human"`. All existing CLI invocations work unchanged.
- **Output format**: Human-readable output changes: (a) InProgress states render in **blue** instead of yellow; (b) type fields gain color. These are visual-only changes with no semantic impact.
- **JSON schema**: Unchanged. `JsonOutputFormatter` schema is preserved exactly.
- **Test assertions**: The single assertion for `\x1b[33m` (Yellow) on `"Active"` state in `HumanOutputFormatterTests` must be updated to `\x1b[34m` (Blue).

### AOT compatibility

All additions must be AOT-safe:
- `OutputFormatterFactory` uses a compile-time `switch` expression — no reflection.
- New `FormatHint`/`FormatInfo` methods return `string` — no serialization or reflection.
- DI registrations use explicit lambda constructors (as already done in `Program.cs`) — no reflection-based service resolution.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| AOT trimmer removes factory or formatter types | Low | High | All types are directly referenced in `Program.cs` DI registrations; trimmer will preserve them. |
| `outputFormat` parameter name collision with `ConsoleAppFramework` reserved words | Low | Medium | ConsoleAppFramework uses `--output` by convention; verify during smoke testing that `--output` is correctly parsed as a string. |
| Command constructor changes break tests in unexpected ways | Medium | Low | Tests use `NSubstitute`; update pattern is mechanical. `HintEngine` with `Hints = false` produces no output side effects. |
| Blue InProgress color is illegible on dark terminals with default blue | Low | Low | Standard ANSI blue (`\x1b[34m`) is universally readable on dark terminals. Light terminals may require user terminal config. `NO_COLOR` support deferred to NG-3. |
| Missing `FormatHint`/`FormatInfo` implementation causes AOT linker to omit interface methods | Very Low | High | All interface methods are called from commands; the linker will preserve all implementations. |

---

## Open Questions

1. **`Console.Write` for confirmation prompts**: `StateCommand` uses `Console.Write(...)` (no newline) for backward/cut confirmation prompts. Should this also go through the formatter? Currently no `FormatPrompt` method exists on `IOutputFormatter`. Recommendation: leave as-is for now; add `FormatPrompt` in a follow-up.

2. **`display.output` as a persistent config key**: Should users be able to set `twig config display.output json` to default to JSON output? Currently NG-1. If desired, this can be added after the wiring is complete by reading `config.Display.Output` as the default for the `output` parameter.

3. **`NO_COLOR` / `TERM=dumb` support**: Some CI environments strip ANSI. Should `HumanOutputFormatter` auto-detect and fall back to plain text? Deferred to NG-3, but tracked here.

4. **`--output` on `version` and `smoke` commands**: These are added as anonymous lambdas in `Program.cs`, not as `TwigCommands` methods. Should they also support `--output`? Low value; recommendation is to leave them as plain `Console.WriteLine`.

5. **`InitCommand` hint wiring**: `InitCommand` has two constructors (production: auth + HTTP; test: `IIterationService`). Adding `OutputFormatterFactory` and `HintEngine` to both constructors is straightforward but increases constructor arity. Acceptable for this plan.

---

## Implementation Phases

### Phase 1 — Color Design Language & Interface Extension
**Goal**: Establish the complete ANSI color vocabulary in `HumanOutputFormatter`, extend `IOutputFormatter` with `FormatHint`/`FormatInfo`, and implement in all three formatters.  
**Exit criteria**: All formatter tests pass; `FormatWorkItem` for `"Active"` state returns blue (`\x1b[34m`); all three formatters implement the full `IOutputFormatter` interface.

### Phase 2 — DI Registration & OutputFormatterFactory
**Goal**: Register all formatters, `OutputFormatterFactory`, and `HintEngine` in DI; add `string output = "human"` to all `TwigCommands` methods.  
**Exit criteria**: `twig status --output json` routes to `JsonOutputFormatter` (verified via smoke test); `twig status` still uses human formatter.

### Phase 3 — Command Refactoring (read commands)
**Goal**: Refactor `StatusCommand`, `TreeCommand`, `WorkspaceCommand`, `SetCommand`, `NavigationCommands`.  
**Exit criteria**: These commands accept `outputFormat` parameter, use formatter, emit HintEngine hints; all related tests pass.

### Phase 4 — Command Refactoring (write commands)
**Goal**: Refactor `StateCommand`, `SaveCommand`, `SeedCommand`, `NoteCommand`, `EditCommand`, `UpdateCommand`, `RefreshCommand`, `ConfigCommand`, `InitCommand`.  
**Exit criteria**: All 9 remaining commands use formatter + HintEngine; all inline hint strings removed; all command tests pass.

### Phase 5 — Wiring Tests
**Goal**: Add `OutputFormatterFactoryTests`, update `HumanOutputFormatterTests` for blue color, add `CommandFormatterWiringTests` for at least three commands.  
**Exit criteria**: Full test suite passes; new tests cover formatter selection, hint suppression, and output routing.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Formatters/OutputFormatterFactory.cs` | Maps format string to `IOutputFormatter` instance; singleton |
| `tests/Twig.Cli.Tests/Formatters/OutputFormatterFactoryTests.cs` | Unit tests for `OutputFormatterFactory` resolution logic |
| `tests/Twig.Cli.Tests/Commands/CommandFormatterWiringTests.cs` | Wiring tests for formatter + hint routing in representative commands |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatHint(string)` and `FormatInfo(string)` method signatures |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add `Blue`, `Magenta` constants; change InProgress color from Yellow→Blue; add `GetTypeColor`; apply to `FormatWorkItem`; add `FormatHint`, `FormatInfo` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatHint` (returns `""`), `FormatInfo` (returns JSON object) |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatHint` (returns `""`), `FormatInfo` (returns raw message) |
| `src/Twig/Program.cs` | Register `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, `OutputFormatterFactory`, `HintEngine`; add `string output = "human"` to all `TwigCommands` methods; pass `output` to each `ExecuteAsync` |
| `src/Twig/Commands/StatusCommand.cs` | Add `OutputFormatterFactory`, `HintEngine`, and `TwigConfiguration` params; `ExecuteAsync(string outputFormat)`; add `workItemRepo.GetSeedsAsync()` + stale seed computation; replace `Console.WriteLine` |
| `src/Twig/Commands/TreeCommand.cs` | Same pattern; remove local `GetShorthand` (use `FormatterHelpers`) |
| `src/Twig/Commands/WorkspaceCommand.cs` | Same pattern |
| `src/Twig/Commands/SetCommand.cs` | Same pattern; remove inline hint string |
| `src/Twig/Commands/NavigationCommands.cs` | Add `outputFormat` param; pass to `setCommand.ExecuteAsync` |
| `src/Twig/Commands/StateCommand.cs` | Same pattern |
| `src/Twig/Commands/SaveCommand.cs` | Same pattern |
| `src/Twig/Commands/SeedCommand.cs` | Same pattern; remove inline hint string; wire `seed` hints |
| `src/Twig/Commands/NoteCommand.cs` | Same pattern; remove inline hint string; wire `note` hints |
| `src/Twig/Commands/EditCommand.cs` | Same pattern; remove inline hint string; wire `edit` hints |
| `src/Twig/Commands/UpdateCommand.cs` | Same pattern |
| `src/Twig/Commands/RefreshCommand.cs` | Same pattern |
| `src/Twig/Commands/ConfigCommand.cs` | Same pattern |
| `src/Twig/Commands/InitCommand.cs` | Add `OutputFormatterFactory`, `HintEngine` to both constructors; same output pattern; remove inline hint string; wire `init` hints via HintEngine |
| `src/Twig/Hints/HintEngine.cs` | Add `"init"` case to `GetHints` switch returning `"Run 'twig set <id>' to set your active work item."` (RD-002) |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Update `FormatWorkItem_ShowsStateWithColor` assertion from `\x1b[33m` → `\x1b[34m`; add tests for `GetTypeColor`, `FormatHint`, `FormatInfo` |
| `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | Add tests for `FormatHint` (returns `""`), `FormatInfo` (valid JSON `{"info":"..."}`) |
| `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | Add tests for `FormatHint` (returns `""`), `FormatInfo` (raw message) |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Add `OutputFormatterFactory`, `HintEngine` (hints disabled), and `TwigConfiguration` to constructor; no logic changes |
| `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/SaveCommandTests.cs` | Same (if exists) |
| `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | Same |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Same |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | No files are deleted |

---

## Implementation Plan

> **Note**: EPICs 001–005 were implemented incrementally across prior commits from other plans. The code changes described below are reflected in the codebase as of the plan's `last_updated` date. This plan serves as post-hoc documentation capturing design rationale, task decomposition, and acceptance criteria for the completed implementation.

### EPIC-001: Color Design Language & Interface Extension

**Goal**: Extend `IOutputFormatter` with `FormatHint`/`FormatInfo`; update ANSI color vocabulary in `HumanOutputFormatter`; implement new methods in all three formatters; update affected tests.

**Prerequisites**: None.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add `FormatHint(string hint)` and `FormatInfo(string message)` to `IOutputFormatter` interface | `src/Twig/Formatters/IOutputFormatter.cs` | DONE |
| ITEM-002 | IMPL | Add `Blue = "\x1b[34m"` and `Magenta = "\x1b[35m"` constants to `HumanOutputFormatter`; update `GetStateColor` to return `Blue` for InProgress states (`active`, `doing`, `committed`, `in progress`, `approved`); add private `GetTypeColor(WorkItemType type)` method — the switch MUST be on `type.Value` (the `string` property of the `readonly record struct`), not on the struct directly (e.g., `type.Value.ToLowerInvariant() switch { "epic" => Magenta, "feature" => Cyan, ... }`); apply `GetTypeColor` in `FormatWorkItem` to the Type field line | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-003 | IMPL | Add `FormatHint` (returns `$"{Dim}  hint: {hint}{Reset}"`) and `FormatInfo` (returns `$"{Dim}{message}{Reset}"`) to `HumanOutputFormatter` | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-004 | IMPL | Add `FormatHint` (returns `""`) and `FormatInfo` (returns `{"info":"<message>"}`) to `JsonOutputFormatter` | `src/Twig/Formatters/JsonOutputFormatter.cs` | DONE |
| ITEM-005 | IMPL | Add `FormatHint` (returns `""`) and `FormatInfo` (returns raw `message`) to `MinimalOutputFormatter` | `src/Twig/Formatters/MinimalOutputFormatter.cs` | DONE |
| ITEM-006 | TEST | Update `FormatWorkItem_ShowsStateWithColor` in `HumanOutputFormatterTests`: change assertion from `\x1b[33m` → `\x1b[34m`; add `FormatWorkItem_ShowsTypeColor_ForEpic` test; add `FormatHint_ReturnsDimHintPrefix` and `FormatInfo_ReturnsDimMessage` tests | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-007 | TEST | Add `FormatHint_ReturnsEmpty` and `FormatInfo_ReturnsJsonObject` tests to `JsonOutputFormatterTests` | `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | DONE |
| ITEM-008 | TEST | Add `FormatHint_ReturnsEmpty` and `FormatInfo_ReturnsRawMessage` tests to `MinimalOutputFormatterTests` | `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `IOutputFormatter` has 9 methods (7 existing + `FormatHint` + `FormatInfo`)
- [x] `HumanOutputFormatter.GetStateColor("active")` returns `\x1b[34m` (Blue)
- [x] `HumanOutputFormatter.FormatWorkItem` for an Epic item includes `\x1b[35m` (Magenta) on the Type line
- [x] `HumanOutputFormatter.FormatHint("test hint")` returns a string containing `"hint:"` and `\x1b[2m`
- [x] All 3 formatter test files pass with no compilation errors

---

### EPIC-002:DI Registration, OutputFormatterFactory, and --output Flag

**Goal**: Create `OutputFormatterFactory`; register all formatters, factory, and `HintEngine` in DI; add `string output = "human"` to all `TwigCommands` methods.

**Prerequisites**: EPIC-001 complete (`IOutputFormatter` interface finalized).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-009 | IMPL | Create `OutputFormatterFactory` with constructor taking `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`; implement `GetFormatter(string format)` returning correct instance via switch expression | `src/Twig/Formatters/OutputFormatterFactory.cs` | DONE |
| ITEM-010 | IMPL | In `Program.cs` `ConfigureServices`: add `services.AddSingleton<HumanOutputFormatter>()`, `services.AddSingleton<JsonOutputFormatter>()`, `services.AddSingleton<MinimalOutputFormatter>()`, `services.AddSingleton<OutputFormatterFactory>()`, `services.AddSingleton<HintEngine>(sp => new HintEngine(sp.GetRequiredService<TwigConfiguration>().Display))` | `src/Twig/Program.cs` | DONE |
| ITEM-011a | IMPL | Add `string output = "human"` parameter to the 8 `TwigCommands` methods whose commands are already refactored (`Set`, `Status`, `Tree`, `Up`, `Down`, `Workspace`, `Show`, `Ws`) and forward to each command's `ExecuteAsync`; remove `output` from the remaining 9 methods (`Init`, `State`, `Seed`, `Note`, `Update`, `Edit`, `Save`, `Refresh`, `Config`) until their commands are updated in EPIC-003/004, to prevent a misleading `--output` CLI flag that silently ignores the value | `src/Twig/Program.cs` | DONE |
| ITEM-011b | IMPL | Add `string output = "human"` parameter to the remaining 9 `TwigCommands` methods (`Init`, `State`, `Seed`, `Note`, `Update`, `Edit`, `Save`, `Refresh`, `Config`) and forward to `ExecuteAsync` after each command is refactored in EPIC-003/004 | `src/Twig/Program.cs` | DEFERRED (EPIC-003/004) |
| ITEM-012 | TEST | Create `OutputFormatterFactoryTests`: test `"json"` → `JsonOutputFormatter`, `"minimal"` → `MinimalOutputFormatter`, `"human"` → `HumanOutputFormatter`, unknown string → `HumanOutputFormatter`, case-insensitive (`"JSON"` → `JsonOutputFormatter`) | `tests/Twig.Cli.Tests/Formatters/OutputFormatterFactoryTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `OutputFormatterFactory.GetFormatter("json")` returns a `JsonOutputFormatter` instance
- [x] `OutputFormatterFactory.GetFormatter("MINIMAL")` returns a `MinimalOutputFormatter` instance
- [x] `OutputFormatterFactory.GetFormatter("xyz")` returns a `HumanOutputFormatter` instance (fallback)
- [x] `HintEngine` is resolvable from the DI container without error
- [x] `dotnet build` succeeds with no new warnings

---

### EPIC-003: Refactor Read Commands — DONE

**Goal**: Refactor `StatusCommand`, `TreeCommand`, `WorkspaceCommand`, `SetCommand`, `NavigationCommands` to use `IOutputFormatter` and `HintEngine`; remove all inline hint strings from these commands; update their tests.

**Prerequisites**: EPIC-001 and EPIC-002 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-013 | IMPL | Refactor `StatusCommand`: (a) add `OutputFormatterFactory formatterFactory`, `HintEngine hintEngine`, and `TwigConfiguration config` constructor params — `TwigConfiguration` is already a singleton in DI (`services.AddSingleton(config)`) and needs no new registration; (b) change `ExecuteAsync()` to `ExecuteAsync(string outputFormat = "human")`; (c) replace all `Console.WriteLine` with `fmt.FormatWorkItem`, `fmt.FormatError`; (d) after fetching the active item, call `var seeds = await workItemRepo.GetSeedsAsync()` then compute `var staleSeedCount = Workspace.Build(item, [], seeds).GetStaleSeeds(config.Seed.StaleDays).Count`; (e) call `hintEngine.GetHints("status", item:item, outputFormat:outputFormat, staleSeedCount:staleSeedCount)` and emit hints | `src/Twig/Commands/StatusCommand.cs` | DONE |
| ITEM-014 | TEST | Update `StatusCommandTests`: add `OutputFormatterFactory`, `HintEngine` (hints disabled), and `TwigConfiguration` (defaults with `Seed.StaleDays = 14`) to command constructor; mock `IWorkItemRepository.GetSeedsAsync()` to return an empty list; verify tests still pass | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | DONE |
| ITEM-015 | IMPL | Refactor `TreeCommand`: same pattern; remove local `GetShorthand` (already in `FormatterHelpers`); use `fmt.FormatTree`; no hint output for `tree` command (not in `HintEngine`) | `src/Twig/Commands/TreeCommand.cs` | DONE |
| ITEM-016 | TEST | Update `TreeNavCommandTests` | `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | DONE |
| ITEM-017 | IMPL | Refactor `WorkspaceCommand`: use `fmt.FormatWorkspace`; call `hintEngine.GetHints("workspace", workspace:workspace, outputFormat:outputFormat)` and emit hints | `src/Twig/Commands/WorkspaceCommand.cs` | DONE |
| ITEM-018 | TEST | Update `WorkspaceCommandTests` | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | DONE |
| ITEM-019 | IMPL | Refactor `SetCommand`: replace all `Console.WriteLine` with `fmt` methods; for the multi-item disambiguation path (where `cached.Count > 1`) that currently writes inline to `Console.Error`, use `fmt.FormatDisambiguation(matches)` where `matches` is `cached.Select(c => (c.Id, c.Title)).ToList()`; remove inline `"  hint: ..."` string; call `hintEngine.GetHints("set", item:item, outputFormat:outputFormat)` and emit hints | `src/Twig/Commands/SetCommand.cs` | DONE |
| ITEM-020 | TEST | Update `SetCommandTests` | `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | DONE |
| ITEM-021 | IMPL | Refactor `NavigationCommands`: add `OutputFormatterFactory formatterFactory` param; change `UpAsync()`/`DownAsync(string)` to accept `string outputFormat = "human"`; pass `outputFormat` to `setCommand.ExecuteAsync`; use `fmt.FormatError` for error messages. Note: `HintEngine` param omitted as it's unused — hints flow through `SetCommand`. | `src/Twig/Commands/NavigationCommands.cs` | DONE |
| ITEM-022 | TEST | Update `TreeNavCommandTests` for NavigationCommands constructor change | `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `StatusCommand`, `TreeCommand`, `WorkspaceCommand`, `SetCommand`, `NavigationCommands` have no raw `Console.WriteLine("...")` calls with inline strings
- [x] No inline `"  hint: ..."` strings remain in these command files
- [x] All 5 command test files pass

---

### EPIC-004: Refactor Write Commands — DONE

**Goal**: Refactor all remaining 9 commands to use `IOutputFormatter` and `HintEngine`; remove all inline hint strings; update their tests.

**Prerequisites**: EPIC-001 and EPIC-002 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-023 | IMPL | Refactor `StateCommand`: add factory + HintEngine params; `ExecuteAsync(string outputFormat = "human")`; replace `Console.Write` prompt (keep as-is per Open Question 1); replace `Console.WriteLine` output with `fmt` methods; call `hintEngine.GetHints("state", item:item, outputFormat:outputFormat, stateShorthand:shorthand, siblings:siblings)` | `src/Twig/Commands/StateCommand.cs` | DONE |
| ITEM-024 | TEST | Update `StateCommandTests` | `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | DONE |
| ITEM-025 | IMPL | Refactor `SaveCommand`: add factory + HintEngine params; use `fmt.FormatSuccess`, `fmt.FormatError`, `fmt.FormatInfo`; no `HintEngine` call (save has no registered hints) | `src/Twig/Commands/SaveCommand.cs` | DONE |
| ITEM-026 | TEST | Update any `SaveCommand` tests in `EditSaveCommandTests.cs` | `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | DONE |
| ITEM-027 | IMPL | Refactor `SeedCommand`: add factory + HintEngine params; replace `Console.WriteLine("Creating...")` with `fmt.FormatInfo`; replace result output with `fmt.FormatSuccess`; remove inline hint string; call `hintEngine.GetHints("seed", outputFormat:outputFormat, createdId:newId)` | `src/Twig/Commands/SeedCommand.cs` | DONE |
| ITEM-028 | TEST | Update `SeedCommandTests` | `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | DONE |
| ITEM-029 | IMPL | Refactor `NoteCommand`: add factory + HintEngine params; replace output with `fmt` methods; remove inline hint string; call `hintEngine.GetHints("note", outputFormat:outputFormat)` | `src/Twig/Commands/NoteCommand.cs` | DONE |
| ITEM-030 | TEST | Update `NoteCommandTests` | `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | DONE |
| ITEM-031 | IMPL | Refactor `EditCommand`: add factory + HintEngine params; replace output with `fmt` methods; remove inline hint string; call `hintEngine.GetHints("edit", outputFormat:outputFormat)` | `src/Twig/Commands/EditCommand.cs` | DONE |
| ITEM-032 | TEST | Update `EditSaveCommandTests` for `EditCommand` constructor | `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | DONE |
| ITEM-033 | IMPL | Refactor `UpdateCommand`: add factory + HintEngine params; replace output with `fmt` methods (no dedicated hint for `update`) | `src/Twig/Commands/UpdateCommand.cs` | DONE |
| ITEM-034 | TEST | Update `UpdateCommandTests` | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | DONE |
| ITEM-035 | IMPL | Refactor `RefreshCommand`: add factory + HintEngine params; replace `Console.WriteLine("Refreshing...", "  Iteration: ...")` with `fmt.FormatInfo`; replace count output with `fmt.FormatSuccess` | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-036 | TEST | Update `RefreshCommandTests` | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | DONE |
| ITEM-037 | IMPL | Refactor `ConfigCommand`: add factory + HintEngine params; replace read/write output with `fmt.FormatSuccess`, `fmt.FormatError` | `src/Twig/Commands/ConfigCommand.cs` | DONE |
| ITEM-038 | TEST | Update `ConfigCommandTests` | `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | DONE |
| ITEM-039 | IMPL | Refactor `InitCommand`: add `OutputFormatterFactory formatterFactory` and `HintEngine hintEngine` to both constructors (production and test); replace all `Console.WriteLine` with `fmt` methods; remove inline `"  hint: Run 'twig set <id>'..."` string; call `hintEngine.GetHints("init", outputFormat:outputFormat)` and emit hints. The `"init"` case is added to `HintEngine` in this same task (see RD-002 in Design Decisions). | `src/Twig/Commands/InitCommand.cs`, `src/Twig/Hints/HintEngine.cs` | DONE |
| ITEM-040 | TEST | Update `InitCommandTests` | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] All 9 commands have no inline `Console.WriteLine("  hint: ...")` strings
- [x] All 9 commands have no raw `Console.WriteLine` calls with inline format strings (only formatter-mediated calls)
- [x] All 9 command test files pass

---

### EPIC-005: Wiring Integration Tests

**Goal**: Verify end-to-end wiring: formatter selection by `OutputFormatterFactory`, hint suppression for json/minimal, output routing through representative commands.

**Prerequisites**: EPIC-003 and EPIC-004 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-041 | TEST | Create `OutputFormatterFactoryTests` (from ITEM-012, ensure comprehensive coverage): `GetFormatter("json")` returns `JsonOutputFormatter`; `GetFormatter("minimal")` returns `MinimalOutputFormatter`; `GetFormatter("human")` returns `HumanOutputFormatter`; `GetFormatter("HUMAN")` case-insensitive returns `HumanOutputFormatter`; `GetFormatter("")` fallback returns `HumanOutputFormatter` | `tests/Twig.Cli.Tests/Formatters/OutputFormatterFactoryTests.cs` | DONE |
| ITEM-042 | TEST | Create `CommandFormatterWiringTests`: (a) `StatusCommand` with `JsonOutputFormatter` — `ExecuteAsync("json")` returns 0 and produces no ANSI escape codes in output (verify by capturing console output via `Console.SetOut`); (b) `StatusCommand` with `HumanOutputFormatter` + hints enabled — `ExecuteAsync("human")` with stale seeds emits a hint string containing `"hint:"`; (c) `StatusCommand` with `MinimalOutputFormatter` — no hint output even with stale seeds (hints suppressed) | `tests/Twig.Cli.Tests/Commands/CommandFormatterWiringTests.cs` | DONE |
| ITEM-043 | TEST | In `CommandFormatterWiringTests`: verify `SetCommand` with `HumanOutputFormatter` and hints enabled calls `HintEngine` and produces hint output after `ExecuteAsync("human")` | `tests/Twig.Cli.Tests/Commands/CommandFormatterWiringTests.cs` | DONE |
| ITEM-044 | TEST | Verify AOT smoke test still passes: `dotnet run --project src/Twig smoke` produces expected output | `tests/Twig.Cli.Tests/AotSmokeTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `OutputFormatterFactoryTests` has ≥5 test cases covering format string resolution and case-insensitivity
- [x] `CommandFormatterWiringTests` has ≥3 wiring tests
- [x] `--output json` produces no ANSI codes (verified in wiring test)
- [x] Full test suite (`dotnet test`) passes with 0 failures

---

## References

- [ADO Work Item State Categories](https://learn.microsoft.com/en-us/azure/devops/boards/work-items/workflow-and-state-categories) — defines Proposed/InProgress/Resolved/Completed/Removed category colors
- [ANSI escape codes reference](https://en.wikipedia.org/wiki/ANSI_escape_code#3-bit_and_4-bit) — standard 3/4-bit color codes
- [ConsoleAppFramework documentation](https://github.com/Cysharp/ConsoleAppFramework) — parameter routing and optional parameter handling
- `docs/projects/twig-epics-1-3.design.md` — prior design document for context on AOT constraints and DI patterns
- `src/Twig/Formatters/IOutputFormatter.cs` — current interface (7 methods)
- `src/Twig/Hints/HintEngine.cs` — hint suppression logic and command name registry
- `src/Twig/Program.cs` — DI registration and `TwigCommands` routing class
