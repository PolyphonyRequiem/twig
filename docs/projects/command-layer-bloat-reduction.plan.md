# Command Layer Bloat Reduction

> **Epic:** #2121 — Domain Critique: Command Layer Bloat Reduction
> **Status**: ✅ Done
> **Revision:** 0 (Initial draft)

---

## Executive Summary

CLI commands in Twig suffer from constructor parameter explosion (up to 17 params), duplicated rendering dispatch logic, inline infrastructure access, and interleaved concerns (orchestration, display, hints, telemetry, sync). This plan introduces a `CommandContext` parameter object to consolidate common dependencies, extracts a `StatusFieldConfigReader` service to eliminate inline `File.ReadAllTextAsync` calls, extracts a shared `TelemetryHelper` to deduplicate the Stopwatch+TrackEvent wrapper pattern, and then propagates these patterns across the 10 most-affected commands. The refactor is purely structural — no behavioral changes, no domain model modifications, no new features. The expected outcome is constructor signatures reduced from 15–17 params to 7–9 params, elimination of 4 duplicated File I/O call sites, and a single telemetry wrapping pattern shared across 10+ commands.

---

## Background

### Current State

All 46 CLI commands are standalone `sealed class` types using C# 12 primary constructors. There is no base class, no shared interface, and no parameter objects. Dependencies are injected flat — every command receives each dependency as a separate constructor parameter.

The `RenderingPipelineFactory` (introduced in EPIC-005) already consolidates the formatter/renderer resolution into a single service, but 10 commands still carry both `OutputFormatterFactory` and `RenderingPipelineFactory?` because the pipeline factory is optional for backward compatibility with pre-EPIC-005 tests.

### Dependency Frequency Analysis

The following dependencies appear across 8+ of the 46 commands:

| Dependency | Usage Count | Category |
|---|---|---|
| `OutputFormatterFactory` | 47 | Rendering |
| `IWorkItemRepository` | 34 | Data Access |
| `TwigConfiguration` | 23 | Configuration |
| `ActiveItemResolver` | 23 | Domain Service |
| `HintEngine` | 19 | UX/Output |
| `IPromptStateWriter?` | 18 | Shell Integration |
| `IAdoWorkItemService` | 16 | External Service |
| `TextWriter? stderr` | 15 | Output/Testing |
| `IContextStore` | 14 | State Management |
| `IGitService?` | 13 | Git Integration |
| `IPendingChangeStore` | 11 | Data Access |
| `IConsoleInput` | 11 | User Interaction |
| `RenderingPipelineFactory?` | 8 | Rendering |
| `ITelemetryClient?` | 8 | Telemetry |

### Commands by Constructor Parameter Count

| Tier | Param Count | Commands |
|---|---|---|
| 🔴 Very High (15+) | 17 | `StatusCommand`, `FlowStartCommand` |
| 🔴 Very High (15+) | 16 | `SetCommand` |
| 🟡 High (11–14) | 13 | `WorkspaceCommand` |
| 🟡 High (11–14) | 12 | `FlowCloseCommand`, `RefreshCommand` |
| 🟡 High (11–14) | 11 | `BatchCommand`, `ShowCommand`, `StateCommand` |
| 🟠 Medium (8–10) | 10 | `BranchCommand`, `FlowDoneCommand`, `NavigationCommands`, `NewCommand`, `SeedNewCommand`, `TreeCommand`, `UpdateCommand` |
| 🟢 Low (≤7) | 2–7 | 30 commands |

### Duplicated Patterns Identified

**1. Rendering Pipeline Resolution (10 commands):**
```csharp
var (fmt, renderer) = pipelineFactory is not null
    ? pipelineFactory.Resolve(outputFormat, noLive)
    : (formatterFactory.GetFormatter(outputFormat), null);
```

**2. Status Field Config Loading (4 call sites across 3 commands + ConfigStatusFieldsCommand):**
```csharp
if (paths is not null && File.Exists(paths.StatusFieldsPath))
{
    var configContent = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
    statusFieldEntries = StatusFieldsConfig.Parse(configContent);
}
```
Found in: `StatusCommand`, `SetCommand`, `ShowCommand`, `ConfigStatusFieldsCommand`.

**3. Telemetry Wrapping (10 commands):**
```csharp
var startTimestamp = Stopwatch.GetTimestamp();
var exitCode = await ExecuteCoreAsync(...);
telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
{
    ["command"] = "...", ["exit_code"] = ..., ["output_format"] = ...,
    ["twig_version"] = ..., ["os_platform"] = ...
}, new Dictionary<string, double> { ["duration_ms"] = ... });
```

**4. Hint Emission (23 commands):**
```csharp
var hints = hintEngine.GetHints("command-name", item: item, outputFormat: outputFormat);
foreach (var hint in hints) {
    var formatted = fmt.FormatHint(hint);
    if (!string.IsNullOrEmpty(formatted)) Console.WriteLine(formatted);
}
```

### Call-Site Audit: Cross-Cutting Impacts

The `CommandContext` parameter object will affect every command that currently receives the dependencies it consolidates. The following table shows the specific commands affected:

| Command | Current Params | Has `OutputFormatterFactory` | Has `RenderingPipelineFactory?` | Has `HintEngine` | Has `TwigConfiguration` | Has `ITelemetryClient?` |
|---|---|---|---|---|---|---|
| `StatusCommand` | 17 | ✅ | ✅ | ✅ | ✅ | ✅ |
| `FlowStartCommand` | 17 | ✅ | ✅ | ✅ | ✅ | ❌ |
| `SetCommand` | 16 | ✅ | ✅ | ✅ | ❌ | ✅ |
| `WorkspaceCommand` | 13 | ✅ | ✅ | ✅ | ✅ | ❌ |
| `FlowCloseCommand` | 12 | ✅ | ❌ | ❌ | ✅ | ❌ |
| `RefreshCommand` | 12 | ✅ | ❌ | ❌ | ✅ | ✅ |
| `BatchCommand` | 11 | ✅ | ❌ | ✅ | ❌ | ❌ |
| `ShowCommand` | 11 | ✅ | ✅ | ❌ | ✅ | ✅ |
| `StateCommand` | 11 | ✅ | ❌ | ✅ | ❌ | ❌ |
| `TreeCommand` | 10 | ✅ | ✅ | ❌ | ✅ | ✅ |

---

## Problem Statement

1. **Constructor parameter explosion**: The top 3 commands have 16–17 constructor parameters. This violates SRP, makes test setup verbose (each test must construct/mock 15+ dependencies), and makes it difficult to reason about what a command actually needs.

2. **Duplicated rendering dispatch**: The `pipelineFactory is not null ? pipelineFactory.Resolve(...) : (formatterFactory.GetFormatter(...), null)` pattern is copy-pasted across 10 commands. The `pipelineFactory` is optional only because older tests predate EPIC-005.

3. **Inline file I/O**: Three commands (`StatusCommand`, `SetCommand`, `ShowCommand`) directly call `File.Exists` and `File.ReadAllTextAsync` on `paths.StatusFieldsPath`. This leaks infrastructure concerns into the command layer and is untestable without real filesystem access.

4. **Duplicated telemetry wrapping**: 10 commands duplicate the same Stopwatch + TrackEvent + Dictionary construction pattern. Each creates identical property dictionaries with `command`, `exit_code`, `output_format`, `twig_version`, `os_platform`.

5. **Interleaved concerns**: A single `ExecuteCoreAsync` method in `StatusCommand` (290 lines) handles resolution, sync, rendering, hint generation, telemetry, and git context enrichment — making it difficult to test individual concerns in isolation.

---

## Goals and Non-Goals

### Goals

1. Reduce constructor parameter counts for the top 10 commands from 10–17 to 7–9 by introducing a `CommandContext` parameter object.
2. Eliminate duplicated status-field config loading by extracting a `StatusFieldConfigReader` service.
3. Eliminate duplicated telemetry wrapping by extracting a `TelemetryHelper` utility.
4. Make the `RenderingPipelineFactory` required (non-nullable) in all commands, eliminating the fallback `formatterFactory.GetFormatter(...)` path.
5. Maintain 100% behavioral compatibility — no user-facing changes, no output changes, no new features.
6. Maintain or improve test coverage — all existing tests must pass without behavioral changes.

### Non-Goals

- Extracting a shared base class or `ICommand` interface (adds coupling without proportional benefit for ConsoleAppFramework commands).
- Refactoring domain model, aggregates, or value objects (separate epic per critique doc).
- Changing the rendering pipeline architecture (IAsyncRenderer vs IOutputFormatter split).
- Adding new commands or features.
- Refactoring the hint engine or consolidating hint emission patterns (lower value, can follow later).
- Touching commands with ≤7 parameters (diminishing returns).

---

## Requirements

### Functional

1. **FR-1: CommandContext parameter object** — A new `CommandContext` record aggregates the most common cross-cutting dependencies (rendering, hints, config, telemetry, stderr). Commands that currently take these individually must accept a single `CommandContext` instead.

2. **FR-2: StatusFieldConfigReader** — A new service encapsulates the `File.Exists` + `File.ReadAllTextAsync` + `StatusFieldsConfig.Parse` pattern behind an interface. All 3 commands that inline this logic must delegate to the service.

3. **FR-3: TelemetryHelper** — A static helper encapsulates the Stopwatch + TrackEvent wrapping pattern. Commands that duplicate this pattern must use the helper.

4. **FR-4: RenderingPipelineFactory required** — The `RenderingPipelineFactory` parameter becomes non-nullable in all commands. The fallback `formatterFactory.GetFormatter(...)` path is removed. Test fixtures must provide a `RenderingPipelineFactory` instance.

5. **FR-5: Backward compatibility** — All existing public `ExecuteAsync` signatures remain unchanged. Return codes, output format, and behavioral semantics are preserved.

### Non-Functional

1. **NFR-1: AOT compatibility** — All new types must be AOT-safe. No reflection, no `dynamic`, no `Activator.CreateInstance`. Parameter objects use concrete types or compile-time-known generics.

2. **NFR-2: Test impact** — Test files will need constructor updates but test logic (assertions, mocking) must not change. Test compilation must succeed.

3. **NFR-3: Build clean** — Zero warnings (`TreatWarningsAsErrors=true`). No new nullable reference type warnings.

---

## Proposed Design

### Architecture Overview

The refactoring introduces three new types and modifies existing command constructors:

```
┌───────────────────────────────────────────────────────┐
│                    CommandContext                       │
│  (sealed record — parameter object)                    │
│                                                        │
│  ├── RenderingPipelineFactory PipelineFactory           │
│  ├── OutputFormatterFactory FormatterFactory            │
│  ├── HintEngine HintEngine                             │
│  ├── TwigConfiguration Config                          │
│  ├── ITelemetryClient? TelemetryClient                 │
│  ├── TextWriter Stderr                                 │
│  │                                                     │
│  └── Methods:                                          │
│      ├── Resolve(format, noLive) → (fmt, renderer)     │
│      ├── EmitHints(command, fmt, hints)                 │
│      └── TrackCommand(command, format, exitCode, start)│
└───────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────┐
│              StatusFieldConfigReader                   │
│  (sealed class — registered in DI)                     │
│                                                        │
│  ctor(TwigPaths paths)                                 │
│                                                        │
│  └── ReadAsync(CancellationToken)                      │
│      → IReadOnlyList<StatusFieldEntry>?                │
│      Encapsulates File.Exists + ReadAllTextAsync +     │
│      StatusFieldsConfig.Parse with best-effort catch   │
└───────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────┐
│                  TelemetryHelper                       │
│  (static class — no DI registration needed)            │
│                                                        │
│  └── TrackCommand(ITelemetryClient?, command, format,  │
│                   exitCode, startTimestamp)             │
│      Encapsulates the Dictionary construction +        │
│      TrackEvent call with version/OS metadata           │
└───────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `CommandContext` (sealed record)

**Location:** `src/Twig/Commands/CommandContext.cs`

```csharp
public sealed record CommandContext(
    RenderingPipelineFactory PipelineFactory,
    OutputFormatterFactory FormatterFactory,
    HintEngine HintEngine,
    TwigConfiguration Config,
    ITelemetryClient? TelemetryClient = null,
    TextWriter? Stderr = null)
{
    public TextWriter StderrWriter => Stderr ?? Console.Error;

    public (IOutputFormatter Formatter, IAsyncRenderer? Renderer) Resolve(
        string outputFormat, bool noLive = false)
        => PipelineFactory.Resolve(outputFormat, noLive);
}
```

**Design decisions:**
- **Record type**: Immutable, value equality, supports `with` expressions for test overrides.
- **`Resolve` convenience method**: Delegates to `PipelineFactory.Resolve` so commands can write `ctx.Resolve(outputFormat)` instead of `ctx.PipelineFactory.Resolve(outputFormat)`.
- **`StderrWriter` property**: Provides a non-null `TextWriter` defaulting to `Console.Error`, eliminating the `stderr ?? Console.Error` pattern repeated in 15+ commands.
- **Includes `OutputFormatterFactory`**: Even though `RenderingPipelineFactory` internally holds a reference to it, several commands need direct formatter access (e.g., `formatterFactory.GetFormatter(...)` without the renderer) for sub-operations like `PendingChangeFlusher`.
- **Does NOT include domain services** (e.g., `ActiveItemResolver`, `IWorkItemRepository`): These vary per command and would make `CommandContext` a god object. Only cross-cutting presentation/infrastructure concerns are included.

**Why not a base class?** ConsoleAppFramework uses source generation to discover command methods. A base class with virtual methods would add complexity without real benefit — the commands share dependencies, not behavior. A parameter object provides the dependency consolidation benefit without coupling commands to an inheritance hierarchy.

#### 2. `StatusFieldConfigReader` (sealed class)

**Location:** `src/Twig/Commands/StatusFieldConfigReader.cs`

```csharp
public sealed class StatusFieldConfigReader(TwigPaths paths)
{
    public async Task<IReadOnlyList<StatusFieldEntry>?> ReadAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(paths.StatusFieldsPath))
            return null;
        try
        {
            var content = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
            return StatusFieldsConfig.Parse(content);
        }
        catch { return null; }
    }
}
```

**Design decisions:**
- **Not behind an interface**: Only 3 call sites, all in the same layer. An interface would be over-abstraction. Tests can construct it with a real temp directory (as `StatusCommandTests` already does) or pass `null` to commands to skip the feature.
- **Best-effort semantics preserved**: Returns `null` on any failure, matching current behavior.

#### 3. `TelemetryHelper` (static class)

**Location:** `src/Twig/Commands/TelemetryHelper.cs`

```csharp
public static class TelemetryHelper
{
    public static void TrackCommand(
        ITelemetryClient? client,
        string command,
        string outputFormat,
        int exitCode,
        long startTimestamp,
        IReadOnlyDictionary<string, string>? extraProperties = null,
        IReadOnlyDictionary<string, double>? extraMetrics = null)
    {
        if (client is null) return;
        var properties = new Dictionary<string, string>
        {
            ["command"] = command,
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = RuntimeInformation.OSDescription
        };
        if (extraProperties is not null)
            foreach (var kvp in extraProperties) properties[kvp.Key] = kvp.Value;

        var metrics = new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        };
        if (extraMetrics is not null)
            foreach (var kvp in extraMetrics) metrics[kvp.Key] = kvp.Value;

        client.TrackEvent("CommandExecuted", properties, metrics);
    }
}
```

**Design decisions:**
- **Static class**: No state, no DI needed. Pure utility.
- **`extraProperties`/`extraMetrics` params**: Some commands (e.g., `RefreshCommand`) add extra telemetry properties like `hash_changed` and `item_count`. The extras params support this without forking the helper.
- **Null guard on `client`**: Callers don't need their own null check.

### Data Flow

**Before (StatusCommand example):**
```
StatusCommand ctor(contextStore, workItemRepo, pendingChangeStore, config,
    formatterFactory, hintEngine, activeItemResolver, workingSetService,
    SyncCoordinatorPair, paths,
    pipelineFactory?, gitService?, adoGitService?, fieldDefinitionStore?,
    telemetryClient?, stderr?, processConfigProvider?)
                     ↓
ExecuteAsync → Stopwatch → ExecuteCoreAsync
    → pipelineFactory?.Resolve() ?? formatterFactory.GetFormatter()
    → File.Exists(paths.StatusFieldsPath) + File.ReadAllTextAsync(...)
    → [render, sync, hints, git context]
    → telemetryClient?.TrackEvent(Dictionary{...}, Dictionary{...})
```

**After:**
```
StatusCommand ctor(ctx: CommandContext, contextStore, workItemRepo,
    pendingChangeStore, activeItemResolver, workingSetService,
    SyncCoordinatorPair, statusFieldReader,
    gitService?, adoGitService?, fieldDefinitionStore?,
    processConfigProvider?)
                     ↓
ExecuteAsync → Stopwatch → ExecuteCoreAsync
    → ctx.Resolve(outputFormat, noLive)
    → statusFieldReader.ReadAsync(ct)
    → [render, sync, hints, git context]
    → TelemetryHelper.TrackCommand(ctx.TelemetryClient, "status", ...)
```

**Parameter reduction: 17 → 12** (removed: `formatterFactory`, `hintEngine`, `config`, `telemetryClient`, `stderr`, `paths`; added: `ctx`, `statusFieldReader`).

### Design Decisions

| Decision | Rationale |
|---|---|
| Parameter object over base class | ConsoleAppFramework source-gen doesn't benefit from base classes. Parameter objects provide the same DI consolidation without inheritance coupling. |
| `CommandContext` as a record | Immutability, value equality, `with` expressions for test customization. |
| `StatusFieldConfigReader` as concrete class (no interface) | Only 3 call sites, all in CLI layer. Interface adds ceremony without value. Tests already use real temp directories for `TwigPaths`. |
| `TelemetryHelper` as static | Stateless utility. No DI registration needed. Null-guarded internally. |
| Incremental command-by-command migration | Critique doc recommends "one at a time — StatusCommand first." Each command can be migrated independently without breaking others. |
| `OutputFormatterFactory` stays in `CommandContext` | Even though `RenderingPipelineFactory` uses it, commands need direct access for sub-operations (e.g., `PendingChangeFlusher` needs a formatter). Hiding it would force consumers to reach through the pipeline factory. |

---

## Dependencies

### External Dependencies
- None. All new types use existing .NET 10 and Spectre.Console APIs.

### Internal Dependencies
- `RenderingPipelineFactory` — already exists, becomes non-nullable.
- `OutputFormatterFactory` — already exists, accessed via `CommandContext`.
- `HintEngine` — already exists, accessed via `CommandContext`.
- `TwigConfiguration` — already exists, accessed via `CommandContext`.
- `ITelemetryClient` — already exists (optional).
- `TwigPaths` — already exists, used by `StatusFieldConfigReader`.
- `StatusFieldsConfig.Parse` — already exists in Domain layer.

### Sequencing Constraints
- The critique doc (Item 8) notes this should happen "after stabilizing the orchestrator layer" (Item 6). However, this refactor is purely structural within the command layer — it does not touch orchestrators, domain services, or the rendering pipeline. It is safe to proceed independently.
- `CommandContext` and `StatusFieldConfigReader` must be created before any command migration begins.
- `StatusCommand` must be migrated first as the pattern-setting exemplar. Other commands follow.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Test churn — 70+ test files touch command constructors | High | Medium | Incremental migration (one command per PR). Each PR updates only the test files for that command. |
| DI registration changes break runtime wiring | Medium | High | Add a `CommandContextFactory` or register `CommandContext` in DI with a factory lambda. Validate with existing DI tests (`CommandRegistrationModuleTests`, `CommandServiceModuleTests`). |
| `RenderingPipelineFactory` becoming required breaks tests that predate EPIC-005 | Medium | Medium | Provide a `TestCommandContext` factory or helper in test infrastructure that creates a default `CommandContext` with a redirected-output `RenderingPipelineFactory`. |
| AOT trim warnings from new types | Low | High | Use sealed records and concrete types only. No generics, no interfaces on the new types. Verify with `dotnet publish` AOT build. |

---

## Open Questions

1. **[Low] Should `CommandContext` include `TwigPaths`?** Currently `TwigPaths` is used by only 5 commands. Including it would further reduce params for those commands but adds a dependency that 38 commands don't need. **Recommendation:** Exclude from `CommandContext`, keep as separate param where needed; `StatusFieldConfigReader` absorbs the primary use case.

2. **[Low] Should hint emission be a method on `CommandContext`?** The hint emission pattern (get hints → format → write) is repeated 23 times but is only 4 lines. A `CommandContext.EmitHints(commandName, fmt, item, ...)` method would consolidate it, but the HintEngine.GetHints signature has many optional params. **Recommendation:** Defer to a follow-up; the hint engine's parameter surface needs its own cleanup first.

3. **[Low] Should we introduce `IStatusFieldConfigReader` interface for testability?** The current approach uses a concrete class. Tests already create real temp directories. An interface enables mock-based testing but adds ceremony for 3 call sites. **Recommendation:** Keep concrete; add interface only if test friction emerges.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig/Commands/CommandContext.cs` | Parameter object consolidating cross-cutting command dependencies |
| `src/Twig/Commands/StatusFieldConfigReader.cs` | Encapsulates status field config file reading |
| `src/Twig/Commands/TelemetryHelper.cs` | Static helper for telemetry event emission |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig/Commands/StatusCommand.cs` | Replace 6 individual params with `CommandContext` + `StatusFieldConfigReader`. Use `TelemetryHelper`. Remove inline File I/O. |
| `src/Twig/Commands/SetCommand.cs` | Replace 5 individual params with `CommandContext` + `StatusFieldConfigReader`. Use `TelemetryHelper`. Remove inline File I/O. |
| `src/Twig/Commands/ShowCommand.cs` | Replace 5 individual params with `CommandContext` + `StatusFieldConfigReader`. Use `TelemetryHelper`. Remove inline File I/O. |
| `src/Twig/Commands/TreeCommand.cs` | Replace 4 individual params with `CommandContext`. Use `TelemetryHelper`. |
| `src/Twig/Commands/WorkspaceCommand.cs` | Replace 3 individual params with `CommandContext`. |
| `src/Twig/Commands/FlowStartCommand.cs` | Replace 4 individual params with `CommandContext`. |
| `src/Twig/Commands/FlowCloseCommand.cs` | Replace 2 individual params with `CommandContext`. |
| `src/Twig/Commands/RefreshCommand.cs` | Replace 4 individual params with `CommandContext`. Use `TelemetryHelper`. |
| `src/Twig/Commands/BatchCommand.cs` | Replace 3 individual params with `CommandContext`. |
| `src/Twig/Commands/StateCommand.cs` | Replace 2 individual params with `CommandContext`. |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `CommandContext` and `StatusFieldConfigReader` in DI. |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Update factory lambdas for commands using `CommandContext`. |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Update constructor calls to use `CommandContext`. |
| `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandDeprecationTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandProfileTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Update constructor calls. |
| `tests/Twig.Cli.Tests/DependencyInjection/CommandRegistrationModuleTests.cs` | Validate `CommandContext` resolves correctly. |
| `tests/Twig.Cli.Tests/DependencyInjection/CommandServiceModuleTests.cs` | Validate new service registrations. |

---

## ADO Work Item Structure

### Epic: #2121 — Domain Critique: Command Layer Bloat Reduction

---

### Issue 1: Introduce CommandContext, StatusFieldConfigReader, and TelemetryHelper

**Goal:** Create the three new types that all subsequent command migrations depend on. Register them in DI. Add unit tests.

**Prerequisites:** None

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| T-2121.1.1 | Create `CommandContext` sealed record with `Resolve` convenience method and `StderrWriter` property | `src/Twig/Commands/CommandContext.cs` | ~40 LoC |
| T-2121.1.2 | Create `StatusFieldConfigReader` sealed class with `ReadAsync` method | `src/Twig/Commands/StatusFieldConfigReader.cs` | ~25 LoC |
| T-2121.1.3 | Create `TelemetryHelper` static class with `TrackCommand` method | `src/Twig/Commands/TelemetryHelper.cs` | ~40 LoC |
| T-2121.1.4 | Register `CommandContext` and `StatusFieldConfigReader` in DI modules | `src/Twig/DependencyInjection/CommandServiceModule.cs` | ~20 LoC |
| T-2121.1.5 | Add unit tests for all three new types | `tests/Twig.Cli.Tests/Commands/CommandContextTests.cs`, `tests/Twig.Cli.Tests/Commands/StatusFieldConfigReaderTests.cs`, `tests/Twig.Cli.Tests/Commands/TelemetryHelperTests.cs` | ~120 LoC |

**Acceptance Criteria:**
- [ ] `CommandContext` record compiles and is AOT-safe
- [ ] `StatusFieldConfigReader.ReadAsync` returns entries when file exists, null when not
- [ ] `TelemetryHelper.TrackCommand` emits correct properties/metrics, is a no-op when client is null
- [ ] DI registration resolves `CommandContext` and `StatusFieldConfigReader` without errors
- [ ] All new types are `sealed`
- [ ] Build passes with zero warnings

---

### Issue 2: Migrate StatusCommand to CommandContext

**Goal:** Refactor `StatusCommand` as the pattern-setting exemplar. Reduce constructor params from 17 to 12. Replace inline File I/O and telemetry boilerplate.

**Prerequisites:** Issue 1

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| T-2121.2.1 | Refactor `StatusCommand` constructor to accept `CommandContext` and `StatusFieldConfigReader`, removing individual params (`formatterFactory`, `hintEngine`, `config`, `telemetryClient`, `stderr`, `paths`) | `src/Twig/Commands/StatusCommand.cs` | ~60 LoC changed |
| T-2121.2.2 | Replace inline `File.Exists`/`File.ReadAllTextAsync` with `statusFieldReader.ReadAsync(ct)` | `src/Twig/Commands/StatusCommand.cs` | ~10 LoC changed |
| T-2121.2.3 | Replace telemetry Dictionary construction with `TelemetryHelper.TrackCommand` | `src/Twig/Commands/StatusCommand.cs` | ~15 LoC changed |
| T-2121.2.4 | Update `CommandRegistrationModule` if StatusCommand has a factory lambda | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~5 LoC changed |
| T-2121.2.5 | Update all StatusCommand test files to use `CommandContext` | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | ~80 LoC changed |

**Acceptance Criteria:**
- [ ] StatusCommand constructor has ≤12 parameters
- [ ] No `File.Exists` or `File.ReadAllTextAsync` calls in StatusCommand
- [ ] No inline telemetry Dictionary construction
- [ ] All existing StatusCommand tests pass unchanged (only constructor calls updated)
- [ ] Build passes with zero warnings

---

### Issue 3: Migrate SetCommand and ShowCommand to CommandContext

**Goal:** Apply the established pattern to `SetCommand` (16 params → 11) and `ShowCommand` (11 params → 8). Both share the status-field-config reading pattern.

**Prerequisites:** Issue 2

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| T-2121.3.1 | Refactor `SetCommand` constructor to accept `CommandContext` and `StatusFieldConfigReader` | `src/Twig/Commands/SetCommand.cs` | ~50 LoC changed |
| T-2121.3.2 | Refactor `ShowCommand` constructor to accept `CommandContext` and `StatusFieldConfigReader` | `src/Twig/Commands/ShowCommand.cs` | ~40 LoC changed |
| T-2121.3.3 | Update DI registration for SetCommand and ShowCommand if needed | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~10 LoC changed |
| T-2121.3.4 | Update all SetCommand and ShowCommand test files | `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs`, `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs`, `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | ~100 LoC changed |

**Acceptance Criteria:**
- [ ] SetCommand constructor has ≤11 parameters
- [ ] ShowCommand constructor has ≤8 parameters
- [ ] No inline `File.Exists`/`File.ReadAllTextAsync` in either command
- [ ] All existing tests pass unchanged (only constructor calls updated)
- [ ] Build passes with zero warnings

---

### Issue 4: Migrate TreeCommand, WorkspaceCommand, and remaining high-param commands

**Goal:** Apply the pattern to `TreeCommand` (10 → 7), `WorkspaceCommand` (13 → 10), `FlowStartCommand` (17 → 13), `FlowCloseCommand` (12 → 10), `RefreshCommand` (12 → 9), `BatchCommand` (11 → 9), `StateCommand` (11 → 9).

**Prerequisites:** Issue 2

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| T-2121.4.1 | Refactor `TreeCommand` constructor | `src/Twig/Commands/TreeCommand.cs` | ~30 LoC changed |
| T-2121.4.2 | Refactor `WorkspaceCommand` constructor | `src/Twig/Commands/WorkspaceCommand.cs` | ~30 LoC changed |
| T-2121.4.3 | Refactor `FlowStartCommand` constructor | `src/Twig/Commands/FlowStartCommand.cs` | ~30 LoC changed |
| T-2121.4.4 | Refactor `FlowCloseCommand`, `RefreshCommand`, `BatchCommand`, `StateCommand` constructors | 4 command files | ~80 LoC changed |
| T-2121.4.5 | Update DI registrations for all migrated commands | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~30 LoC changed |
| T-2121.4.6 | Update all test files for migrated commands | 15+ test files | ~250 LoC changed |

**Acceptance Criteria:**
- [ ] All 7 commands have reduced constructor parameter counts as specified
- [ ] All existing tests pass unchanged (only constructor calls updated)
- [ ] DI resolution tests pass
- [ ] Build passes with zero warnings
- [ ] AOT publish succeeds

---

## PR Groups

### PG-1: Foundation — CommandContext, StatusFieldConfigReader, TelemetryHelper + StatusCommand migration

**Issues covered:** Issue 1 + Issue 2
**Classification:** Deep (few files, complex design decisions)
**Estimated LoC:** ~500 changed/added
**Estimated files:** ~10

**Rationale:** The foundation types and the pattern-setting StatusCommand migration belong in one PR because reviewers need to see the abstractions applied to understand their design. StatusCommand is the most complex command and validates that CommandContext works for the hardest case.

**Contents:**
- 3 new source files (`CommandContext.cs`, `StatusFieldConfigReader.cs`, `TelemetryHelper.cs`)
- 3 new test files
- `StatusCommand.cs` refactored
- DI module updates
- StatusCommand test files updated

**Successor:** PG-2

---

### PG-2: Propagation — Migrate remaining 9 commands to CommandContext

**Issues covered:** Issue 3 + Issue 4
**Classification:** Wide (many files, mechanical pattern application)
**Estimated LoC:** ~800 changed
**Estimated files:** ~35

**Rationale:** Once the pattern is established in PG-1, all remaining command migrations are mechanical — replace individual params with `CommandContext`, update DI registrations, update test constructors. These can be reviewed as a batch because each command follows the exact same pattern.

**Contents:**
- `SetCommand.cs`, `ShowCommand.cs`, `TreeCommand.cs`, `WorkspaceCommand.cs`, `FlowStartCommand.cs`, `FlowCloseCommand.cs`, `RefreshCommand.cs`, `BatchCommand.cs`, `StateCommand.cs` refactored
- DI registration updates
- 20+ test files updated

**Successor:** None (terminal)

---

## References

- `docs/architecture/domain-model-critique.md` — Item 8 (Command Layer Bloat)
- `docs/architecture/commands.md` — CLI command architecture documentation
- `src/Twig/Rendering/RenderingPipelineFactory.cs` — Existing rendering abstraction
- `src/Twig/DependencyInjection/CommandRegistrationModule.cs` — Current DI wiring
- `src/Twig/DependencyInjection/CommandServiceModule.cs` — Service registrations

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|---|---|---|---|---|
| PG-1-foundation | Foundation — New types + StatusCommand migration | Issue 1 (T-2121.1.1–1.5) + Issue 2 (T-2121.2.1–2.5) | None | Deep |
| PG-2-propagation | Propagation — Migrate remaining 9 commands | Issue 3 (T-2121.3.1–3.4) + Issue 4 (T-2121.4.1–4.6) | PG-1-foundation | Wide |

### Execution Order

**PG-1-foundation (deep, ~500 LoC, ~10 files)**
Introduces all three new abstractions (`CommandContext`, `StatusFieldConfigReader`, `TelemetryHelper`), wires them into DI, and migrates `StatusCommand` as the pattern-setting exemplar. Reviewers see the design decisions and their first real application in the hardest command. This PR must merge before PG-2 begins because every subsequent migration consumes `CommandContext`.

**PG-2-propagation (wide, ~800 LoC, ~35 files)**
Applies the established pattern mechanically to the remaining nine commands (`SetCommand`, `ShowCommand`, `TreeCommand`, `WorkspaceCommand`, `FlowStartCommand`, `FlowCloseCommand`, `RefreshCommand`, `BatchCommand`, `StateCommand`). All changes follow the same substitution pattern: replace individual constructor params with `CommandContext`, swap inline telemetry with `TelemetryHelper.TrackCommand`, delegate File I/O to `StatusFieldConfigReader` where applicable, and update the corresponding test constructors. Reviewable as a batch because each command is a self-similar change.

### Validation Strategy

**PG-1-foundation**
- `dotnet build` — zero warnings, AOT-safe types
- Unit tests for `CommandContext`, `StatusFieldConfigReader`, `TelemetryHelper` (new test files)
- `StatusCommandTests` + `StatusCommand_CacheAwareTests` — all pass with updated constructors
- `CommandServiceModuleTests` — `CommandContext` and `StatusFieldConfigReader` resolve without error
- `dotnet publish` with AOT profile — no trim warnings on new types

**PG-2-propagation**
- `dotnet build` — zero warnings
- All command test suites for the 9 migrated commands pass (constructor-only changes)
- `CommandRegistrationModuleTests` — all 9 command factory lambdas resolve correctly
- `CommandServiceModuleTests` — no regressions
- `dotnet publish` with AOT profile — end-to-end AOT succeeds

---

## Completion

> **Completed:** 2026-04-28
> **Version Tag:** v0.61.0

All 4 Issues and 15 Tasks completed across two PR groups:

- **PG-1** (PR #116, merged 2026-04-28T02:40:03Z): Introduced `CommandContext`, `StatusFieldConfigReader`, and `TelemetryHelper` with full test coverage. Migrated `StatusCommand` as the pattern-setting exemplar.
- **PG-2** (PR #119, merged 2026-04-28T04:08:25Z): Propagated the pattern to `SetCommand`, `ShowCommand`, `TreeCommand`, `WorkspaceCommand`, `FlowStartCommand`, `FlowCloseCommand`, `RefreshCommand`, `BatchCommand`, and `StateCommand`. All DI registrations and test files updated.

Constructor parameter counts reduced as planned. No behavioral changes, all existing tests pass.
