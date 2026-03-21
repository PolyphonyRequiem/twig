# Twig CLI — Comprehensive Architecture Analysis

**Date:** 2026-03-19
**Scope:** `src/` — Twig, Twig.Domain, Twig.Infrastructure, Twig.Tui
**Codebase Metrics:** 162 source files, 15,286 lines of production code, ~30,000 lines of test code (2,048 tests)
**Runtime:** .NET 10, Native AOT, SQLite (WAL mode)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Project Structure and Layer Boundaries](#2-project-structure-and-layer-boundaries)
3. [Dependency Direction and Coupling Analysis](#3-dependency-direction-and-coupling-analysis)
4. [DI / Composition Root Design](#4-di--composition-root-design)
5. [Command Infrastructure](#5-command-infrastructure)
6. [Domain Model Design](#6-domain-model-design)
7. [Infrastructure Layer](#7-infrastructure-layer)
8. [Rendering and Formatting Pipeline](#8-rendering-and-formatting-pipeline)
9. [Gatekeeper Findings — Contextualized](#9-gatekeeper-findings--contextualized)
10. [Architectural Risks and Technical Debt](#10-architectural-risks-and-technical-debt)
11. [Prioritized Recommendations](#11-prioritized-recommendations)
12. [Appendix — Metrics and References](#appendix--metrics-and-references)

---

## 1. Executive Summary

Twig is a command-line tool for Azure DevOps work item management. It follows a **layered architecture** with a clean separation between Domain, Infrastructure, and CLI presentation layers. The overall design is sound: dependency direction flows inward toward the domain, the domain has zero external package dependencies, and the codebase is well-tested (2,048 tests, ~2:1 test-to-production line ratio).

**Key strengths:**
- Clean domain layer with command-queue aggregate pattern, discriminated unions, and value objects
- AOT-first design with reflection-free serialization and factory-based DI
- Dual rendering pipeline (sync ANSI + async Spectre.Console progressive rendering)
- Comprehensive test coverage across all four projects

**Key concerns:**
- Sync-over-async anti-patterns in DI factories and `PromptStateWriter` (H-001, H-002, H-003)
- Significant cross-cutting concern duplication across 33+ commands (M-001 through M-004)
- State-to-color mapping correctness bug from inconsistent state entry propagation (H-003)
- Command registration monolith at 282 lines of manual DI wiring (L-003)

The Gatekeeper anti-pattern review identified 15 findings (3 HIGH, 8 MEDIUM, 4 LOW). These findings are consistent with the broader architectural patterns observed and cluster around three systemic themes: **sync-over-async in composition**, **cross-cutting concern duplication in commands**, and **state/color mapping divergence** across rendering paths.

---

## 2. Project Structure and Layer Boundaries

### 2.1 Project Topology

```
┌──────────────────────────────────────────────────────────────┐
│                     Entry Points                             │
│  ┌────────────────┐              ┌────────────────────────┐  │
│  │    Twig (CLI)   │              │     Twig.Tui (TUI)    │  │
│  │  Program.cs     │              │   Program.cs           │  │
│  │  Commands/      │              │   Views/               │  │
│  │  Formatters/    │              │   Terminal.Gui          │  │
│  │  Rendering/     │              └──────────┬─────────────┘  │
│  │  Hints/         │                         │                │
│  │  DI/            │                         │                │
│  └───────┬─────────┘                         │                │
│          │                                   │                │
├──────────┼───────────────────────────────────┼────────────────┤
│          ▼                                   ▼                │
│  ┌───────────────────────────────────────────────────────┐    │
│  │              Twig.Infrastructure                       │    │
│  │  Ado/ (REST client, DTOs, exceptions)                  │    │
│  │  Auth/ (PAT, AzCLI providers)                          │    │
│  │  Config/ (TwigConfiguration, PromptStateWriter)        │    │
│  │  Git/ (GitCliService, HookInstaller)                   │    │
│  │  GitHub/ (Release client, SelfUpdater)                 │    │
│  │  Persistence/ (SQLite stores)                          │    │
│  │  Serialization/ (AOT JSON context)                     │    │
│  │  DI/ (NetworkServiceModule)                            │    │
│  └──────────────────────┬────────────────────────────────┘    │
│                         │                                     │
├─────────────────────────┼─────────────────────────────────────┤
│                         ▼                                     │
│  ┌───────────────────────────────────────────────────────┐    │
│  │              Twig.Domain (pure domain)                  │    │
│  │  Aggregates/ (WorkItem, ProcessConfiguration)          │    │
│  │  Commands/ (ChangeState, UpdateField, AddNote)         │    │
│  │  ValueObjects/ (WorkItemType, AreaPath, StateEntry...) │    │
│  │  Services/ (ActiveItemResolver, SyncCoordinator...)    │    │
│  │  Interfaces/ (IWorkItemRepository, IAdoWorkItemService)│    │
│  │  ReadModels/ (Workspace, WorkTree, SprintHierarchy)    │    │
│  │  Common/ (Result<T>)                                   │    │
│  │  Enums/ (StateCategory, TransitionKind)                │    │
│  └───────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────┘
```

### 2.2 Layer Responsibilities

| Layer | Project | Responsibility | File Count | Lines |
|-------|---------|----------------|------------|-------|
| **Presentation** | `Twig` | CLI commands, output formatting, Spectre rendering, hints, DI composition | 51 | 7,872 |
| **Presentation** | `Twig.Tui` | Full-screen TUI (Terminal.Gui v2), tree navigation, form editing | 3 | 542 |
| **Infrastructure** | `Twig.Infrastructure` | ADO REST client, SQLite persistence, Git CLI wrapper, auth, config | 41 | 4,091 |
| **Domain** | `Twig.Domain` | Aggregates, value objects, domain services, interfaces, read models | 67 | 2,781 |

### 2.3 Layer Boundary Assessment

**Domain layer** — Excellent isolation. Zero NuGet package references. All external dependencies expressed as interfaces (`IAdoWorkItemService`, `IWorkItemRepository`, etc.). The domain defines 16 interfaces that infrastructure and presentation implement or consume.

**Infrastructure layer** — Clean implementation of domain interfaces. References only `Twig.Domain` and its own NuGet dependencies (Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection). Does **not** reference Spectre.Console, keeping rendering concerns out. One concern: `PromptStateWriter` in Infrastructure.Config performs synchronous I/O plus sync-over-async calls — it straddles the boundary between infrastructure and presentation concerns.

**CLI layer** — References both Domain and Infrastructure. This is expected for a composition root. The CLI layer owns: command dispatch, output formatting, Spectre rendering, hints, and DI wiring. One notable coupling: some command classes directly reference `Infrastructure.Config.TwigConfiguration` (a concrete class, not an interface) for configuration reads.

**TUI layer** — References Domain and Infrastructure. Uses shared `TwigServiceRegistration.AddTwigCoreServices()` for DI, but does **not** share the CLI's command infrastructure, formatters, or rendering. This means the TUI is a separate entry point that reuses the core services but implements its own UI layer.

---

## 3. Dependency Direction and Coupling Analysis

### 3.1 Project Reference Graph

```
Twig ──→ Twig.Domain
Twig ──→ Twig.Infrastructure ──→ Twig.Domain
Twig.Tui ──→ Twig.Domain
Twig.Tui ──→ Twig.Infrastructure ──→ Twig.Domain
```

Dependency direction is **strictly inward**: both presentation projects depend on Infrastructure and Domain; Infrastructure depends only on Domain; Domain depends on nothing. There are no circular references and no upward dependencies. This is the correct dependency direction for a layered architecture.

### 3.2 InternalsVisibleTo Topology

The `InternalsVisibleTo` declarations reveal the intended visibility boundaries:

- **Twig.Domain** → `Twig.Domain.Tests`, `Twig.Infrastructure`, `Twig.Infrastructure.Tests`, `Twig.Cli.Tests`, `Twig.Tui`, `Twig.Tui.Tests` — This is notably broad. Domain internals are visible to Infrastructure (necessary for `SetField`, `SetDirty`, etc.), but also to CLI tests and the TUI. This is pragmatic given the aggregate command pattern, but it does weaken encapsulation.

- **Twig.Infrastructure** → `Twig.Infrastructure.Tests`, `Twig`, `Twig.Cli.Tests` — Infrastructure internals are visible to the CLI host (needed for `LegacyDbMigrator`, `AdoRemoteParser`).

- **Twig** → `Twig.Cli.Tests` — Standard for testing internal commands.

### 3.3 Coupling Hotspots

1. **`TwigConfiguration` (concrete)** — Used directly in 15+ command constructors. Not abstracted behind an interface. Since it's a POCO loaded from JSON, this is pragmatic but creates a direct dependency from commands to `Twig.Infrastructure.Config`.

2. **`OutputFormatterFactory` + `HintEngine`** — Injected into every command. These are cross-cutting concerns that could benefit from a pipeline/middleware pattern.

3. **`ActiveItemResolver`** — Used by 19 commands. This is intentionally centralized (replacing duplicated cache-hit → auto-fetch logic) and is a positive pattern.

---

## 4. DI / Composition Root Design

### 4.1 Overall Structure

The DI composition is distributed across five modules:

| Module | Location | Responsibility |
|--------|----------|----------------|
| `TwigServiceRegistration.AddTwigCoreServices()` | Infrastructure | Config, paths, SQLite, repositories, process config, git service, prompt writer |
| `NetworkServiceModule.AddTwigNetworkServices()` | Infrastructure | Auth, HTTP, ADO clients, iteration service |
| `RenderingServiceModule.AddTwigRenderingServices()` | CLI | Formatters, Spectre.Console, theme, renderer |
| `CommandServiceModule.AddTwigCommandServices()` | CLI | HintEngine, ActiveItemResolver, SyncCoordinator, WorkingSetService |
| `CommandRegistrationModule.AddTwigCommands()` | CLI | All 22+ command class registrations |

**Composition order** in `Program.cs`:
```
services.AddTwigCoreServices();          // 1. Core (config, persistence)
LegacyDbMigrator.MigrateIfNeeded(...);   // 2. Migration (before network)
services.AddTwigNetworkServices(...);    // 3. Network (ADO, auth)
services.AddTwigRenderingServices();     // 4. Rendering (Spectre)
services.AddTwigCommandServices();       // 5. Domain services
services.AddTwigCommands();              // 6. Commands
```

### 4.2 Factory Lambda Pattern

All registrations use factory lambdas (`services.AddSingleton(sp => ...)`) rather than type-only registration. This is an **intentional AOT constraint**: the `Microsoft.Extensions.DependencyInjection` generic resolution via reflection is not AOT-safe. Every factory explicitly constructs its type. This is verbose (CommandRegistrationModule is 282 lines) but correct for Native AOT.

### 4.3 Singleton Lifetime

Every service is registered as `Singleton`. This is appropriate for a short-lived CLI process — the container is built once, commands resolve once, and the process exits. There's no risk of captive dependency or scoping issues. The `HttpClient` singleton is likewise acceptable given the process-lifetime model.

### 4.4 Lazy Command Resolution

Commands are resolved lazily from `IServiceProvider` through the `TwigCommands` façade:

```csharp
public async Task<int> Set([Argument] string idOrPattern, ...)
    => await services.GetRequiredService<SetCommand>().ExecuteAsync(...);
```

This is a deliberate design choice: SQLite-dependent services are only constructed when a command runs. This allows `twig init` (which creates the `.twig/` directory) to run before the database exists.

### 4.5 DI Concerns

- **[H-001] Sync-over-async in `RenderingServiceModule`**: The `SpectreTheme` factory calls `Task.Run(() => processTypeStore.GetAllAsync()).GetAwaiter().GetResult()` to load state entries. This is a sync-over-async anti-pattern that risks thread pool starvation. The mitigation via `Task.Run` avoids deadlock but wastes a thread.

- **Sync-over-async in `TwigServiceRegistration`** (not a numbered Gatekeeper finding): The `TwigConfiguration` factory uses `.GetAwaiter().GetResult()` on `LoadAsync()`. This runs during container build, before any async pipeline is available. While currently harmless in a synchronous bootstrap context, it sets a precedent and shares the same root cause as H-001.

- **[L-003] CommandRegistrationModule monolith**: At 282 lines of hand-wired DI, this is the largest single file in the DI layer. Constructor parameter lists of 10-14 dependencies per command make this fragile to maintain.

---

## 5. Command Infrastructure

### 5.1 Command Pattern

Twig uses a **service-based command pattern**: each command is a plain class with an `ExecuteAsync` method, resolved from DI. There is no shared command interface or base class. Commands return `Task<int>` (exit code).

```
TwigCommands (façade) → GetRequiredService<T>() → T.ExecuteAsync()
```

The façade `TwigCommands` maps ConsoleAppFramework CLI verbs to service calls. This decouples the CLI framework from command implementation.

### 5.2 ConsoleAppFramework Integration

ConsoleAppFramework v5.7.13 provides:
- Zero-allocation command-line parsing via source generation
- Attribute-based command registration (`[Command]`, `[Argument]`, `[Hidden]`)
- Filter pipeline (`ConsoleAppFilter`) — used for the global `ExceptionFilter`
- No reflection for argument binding (AOT-compatible)

The integration is clean: `ConsoleApp.Create()` builds the container, `TwigCommands` is added, and `app.Run(args)` dispatches.

### 5.3 Cross-Cutting Concerns in Commands

This is the most significant architectural weakness. Several cross-cutting patterns are duplicated across commands:

#### 5.3.1 ActiveItemResult Pattern-Matching (M-003)

The `ActiveItemResult` discriminated union is pattern-matched in **19 commands** with near-identical switch expressions:

```csharp
switch (resolveResult)
{
    case ActiveItemResult.Found found: item = found.WorkItem; break;
    case ActiveItemResult.FetchedFromAdo fetched: item = fetched.WorkItem; break;
    case ActiveItemResult.Unreachable unreachable:
        Console.Error.WriteLine(fmt.FormatError(...)); return 1;
    default:
        Console.Error.WriteLine(fmt.FormatError(...)); return 1;
}
```

This 8-12 line block is copied into `BranchCommand`, `CommitCommand`, `EditCommand`, `FlowCloseCommand`, `FlowDoneCommand`, `GitContextCommand`, `NavigationCommands`, `NoteCommand`, `PrCommand`, `SaveCommand`, `SeedCommand`, `SetCommand`, `StashCommand`, `StateCommand`, `StatusCommand`, `TreeCommand`, `UpdateCommand`, `WorkspaceCommand`, and more.

#### 5.3.2 Git Repo Check Boilerplate (M-002)

Commands that interact with Git (Branch, Commit, PR, FlowStart, FlowClose, FlowDone, Log, Stash, GitContext, Hooks, Status) repeat a guard pattern:

```csharp
if (gitService is null) { ... error ... return 1; }
var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
if (!isInWorkTree) { ... error ... return 1; }
```

This appears in **11 commands**.

#### 5.3.3 CancellationToken Propagation (M-001)

Of 35 command entry points in `TwigCommands` (33 excluding aliases), only **5** accept `CancellationToken`: `Set`, `Up`, `Down`, `Upgrade`, and `Changelog`. The remaining ~28 non-alias commands cannot be cancelled, which means Ctrl+C during a network call won't interrupt cleanly.

#### 5.3.4 Magic String `"human"` Default (L-004)

The string literal `"human"` appears as the default `output` parameter in all 30+ command signatures in `TwigCommands` (those that accept an output format). If the default format ever changed, every signature would need updating.

### 5.4 Filter Pipeline

The global `ExceptionFilter` wraps all commands and maps specific exception types to exit codes and stderr messages:

- `OperationCanceledException` → exit 130
- `AdoOfflineException` → "ADO unreachable" + exit 1
- `AdoAuthenticationException` → auth guidance + exit 1
- `AdoNotFoundException` → "not found" + exit 1
- `AdoBadRequestException` → "bad request" + exit 1
- `EditorNotFoundException` → editor guidance + exit 1
- `SqliteException` → "cache corrupted" + exit 1

This is a well-structured centralized error handler. The `ExceptionHandler` is extracted as a static class for testability.

---

## 6. Domain Model Design

### 6.1 Aggregate: WorkItem

`WorkItem` is the root aggregate, implementing a **command-queue pattern**:

```
ChangeState(newState) → enqueue ChangeStateCommand → IsDirty = true
UpdateField(field, value) → enqueue UpdateFieldCommand → IsDirty = true
AddNote(note) → enqueue AddNoteCommand → IsDirty = true
ApplyCommands() → dequeue all → execute → return FieldChanges
```

This is a well-designed pattern for offline-first mutation. Commands accumulate locally, and `ApplyCommands()` flushes them before sync. The `IsDirty` flag integrates with the pending-change store for persistence.

**Strengths:**
- Clean separation of command enqueueing and application
- Internal mutators (`SetField`, `SetDirty`) are scoped via `internal` access
- ReadOnlyDictionary/ReadOnlyCollection for public surfaces
- Negative sentinel IDs for seeds (not-yet-persisted items)

**Concerns:**
- `State` has `internal set` allowing Infrastructure to mutate it directly — this bypasses the command queue. Could indicate a needed refactoring path.
- `WorkItemType`, `AreaPath`, `IterationPath` are value objects with `init` setters, making WorkItem partially immutable. The command-queue pattern adds controlled mutability.

### 6.2 Value Objects

The domain defines 15 value objects, including:
- `WorkItemType` — Parsed with validation via `Result<T>`
- `StateEntry` — Carries state name, category, color, and order
- `FieldChange` — Records old/new values for change tracking
- `BranchNameTemplate` — Template + slug generation for git branches
- `IconSet` — Badge glyph resolution chain (nerd fonts → unicode → ASCII)
- `PullRequestInfo`, `PullRequestCreate` — ADO PR DTOs

### 6.3 Domain Services

22 domain services handle orchestration logic:

| Service | Role |
|---------|------|
| `ActiveItemResolver` | Context → cache lookup → ADO auto-fetch |
| `SyncCoordinator` | Working set sync with staleness checking |
| `ProtectedCacheWriter` | Prevents overwriting dirty items during sync |
| `WorkingSetService` | Computes sprint items, seeds, and dirty state |
| `BacklogHierarchyService` | Builds parent-child tree from flat item list |
| `StateCategoryResolver` | State name → category mapping (ADO entries + heuristic fallback) |
| `TypeColorResolver` | Type name → hex color (user override → ADO appearance → deterministic) |
| `StateTransitionService` | Validates state transitions against process configuration |
| `ConflictResolver` | Detects field-level conflicts between local and remote |
| `BranchNamingService` | Generates branch names from templates |
| `CommitMessageService` | Generates commit messages from templates |
| `PatternMatcher` | Fuzzy title matching for `twig set <pattern>` |
| `DynamicProcessConfigProvider` | Wraps `IProcessTypeStore` as `IProcessConfigurationProvider` |

### 6.4 Read Models

Three read models represent pre-computed views:
- `Workspace` — Active item + sprint items + seeds + dirty summary + sprint hierarchy
- `WorkTree` — Parent chain + focused item + children (tree view)
- `SprintHierarchy` — Assignee-grouped tree of sprint items with parent context nodes

### 6.5 Result<T> Pattern

`Result<T>` is a readonly record struct providing explicit success/failure:

```csharp
public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    public T Value { get; }  // throws on failure
    public string Error { get; }
}
```

Used primarily for `WorkItemType.Parse()`. This is a clean alternative to exceptions for expected failures.

### 6.6 ActiveItemResult Discriminated Union

```csharp
public abstract record ActiveItemResult
{
    public sealed record Found(WorkItem WorkItem) : ActiveItemResult;
    public sealed record NoContext : ActiveItemResult;
    public sealed record FetchedFromAdo(WorkItem WorkItem) : ActiveItemResult;
    public sealed record Unreachable(int Id, string Reason) : ActiveItemResult;
}
```

Well-designed exhaustive union. The pattern-match duplication (M-003) is a consumption problem, not a design problem.

---

## 7. Infrastructure Layer

### 7.1 Persistence — SQLite

`SqliteCacheStore` manages the SQLite lifecycle:
- **WAL mode** + 5-second busy timeout for concurrent access
- **Schema versioning** — `SchemaVersion = 4` with auto-rebuild on mismatch
- **Single connection** per CLI invocation (no thread-safety needed)
- **5 tables**: `metadata`, `work_items`, `pending_changes`, `process_types`, `context`
- **7 indexes** including partial indexes for dirty and seed items

Six repository/store classes wrap the connection:
- `SqliteWorkItemRepository` (IWorkItemRepository)
- `SqliteContextStore` (IContextStore)
- `SqlitePendingChangeStore` (IPendingChangeStore)
- `SqliteProcessTypeStore` (IProcessTypeStore)
- `SqliteUnitOfWork` (IUnitOfWork)
- `SqliteCacheStore` (connection owner, schema management)

**Assessment:** The SQLite persistence is well-designed for an offline-first CLI. WAL mode provides good read concurrency for prompt state reads while the CLI is running. Schema versioning with auto-rebuild is appropriate for a cache (data can be reconstructed from ADO). The single-connection model avoids complexity.

### 7.2 ADO Integration

`AdoRestClient` implements `IAdoWorkItemService` via the ADO REST API (v7.1):
- Fetch individual work items with relation expansion
- Fetch children via WIQL queries
- Create/update work items via JSON Patch
- Create pull requests, add comments, link artifacts

Supporting types:
- `AdoGitClient` — PR creation, branch linking, repo operations
- `AdoIterationService` — Sprint iteration queries
- `AdoRemoteParser` — Parses ADO git remote URLs for org/project/repo extraction
- `AdoResponseMapper` — Maps REST DTOs → domain `WorkItem` aggregates
- `AdoErrorHandler` — Centralizes HTTP error → exception mapping
- 14 DTO types for request/response serialization

`AdoRestClient` uses `TwigJsonContext` (source-generated `JsonSerializerContext`) for AOT-safe serialization.

### 7.3 Git Integration

`GitCliService` implements `IGitService` by spawning `git` subprocesses:
- Branch operations (current branch, create, checkout, delete)
- Commit operations (commit, stash, stash pop)
- Repository queries (is-inside-work-tree, remote URL, log)

`HookInstaller` manages git hook scripts:
- Installs/uninstalls Twig-managed hooks (prepare-commit-msg, commit-msg, post-checkout)
- Creates shell scripts that invoke `twig _hook <hook-name>`
- Preserves existing hooks via comment-based ownership detection

### 7.4 Authentication

Two providers implement `IAuthenticationProvider`:
- `PatAuthProvider` — Personal Access Token from `$TWIG_PAT` env var
- `AzCliAuthProvider` — Azure CLI token acquisition via `az account get-access-token`

Selected at DI time based on `config.auth.method` (default: `"azcli"`).

### 7.5 Configuration

`TwigConfiguration` is a POCO loaded from `.twig/config` (JSON). Notable design:
- `SetValue(dotPath, value)` uses a switch expression on ~25 known paths — reflection-free and AOT-safe
- **[M-008] No validation** for enum-like values in some paths (e.g., `auth.method` accepts any string)
- Nested config sections: `Auth`, `Defaults`, `Seed`, `Display`, `User`, `Git`, `Flow`
- `TypeAppearances` list for ADO-synced type metadata (color, iconId)

### 7.6 Serialization

`TwigJsonContext` is a source-generated `JsonSerializerContext` providing AOT-compatible serialization for all DTOs and configuration types. This eliminates reflection-based JSON serialization entirely, which is required for `PublishAot=true`.

---

## 8. Rendering and Formatting Pipeline

### 8.1 Dual Pipeline Architecture

Twig has two rendering paths:

1. **Sync Formatter Path** (`IOutputFormatter`): String-based formatting with ANSI escape codes. Used for JSON output, minimal output, piped output, and `--no-live` mode.

2. **Async Renderer Path** (`IAsyncRenderer` / `SpectreRenderer`): Spectre.Console-based progressive rendering with live updates. Used for human output on interactive terminals.

```
RenderingPipelineFactory.Resolve(format, noLive)
├── format="human" + TTY + !noLive → (HumanFormatter, SpectreRenderer)
├── format="human" + redirected  → (HumanFormatter, null)
├── format="json"                → (JsonFormatter, null)
└── format="minimal"             → (MinimalFormatter, null)
```

### 8.2 Output Formatters

Three `IOutputFormatter` implementations:

| Formatter | Output Style | Use Case |
|-----------|-------------|----------|
| `HumanOutputFormatter` | ANSI colors, box-drawing, aligned columns | Interactive terminal |
| `JsonOutputFormatter` | Stable JSON schema | Scripting, automation |
| `MinimalOutputFormatter` | Tab-separated, no decoration | Piping, `grep`, `awk` |

The `IOutputFormatter` interface defines 13 methods covering work items, trees, workspaces, errors, hints, git context, and log entries.

### 8.3 Theme System

Two parallel theme systems exist:

1. **`HumanOutputFormatter` ANSI theme** — Uses raw ANSI escape codes (`\x1b[31m`, etc.). Color resolution: `TypeColorResolver.ResolveHex()` → `HexToAnsi.ToForeground()` → `DeterministicTypeColor.GetAnsiEscape()`.

2. **`SpectreTheme` Spectre theme** — Uses Spectre.Console `Style` objects and markup strings. Color resolution: `TypeColorResolver.ResolveHex()` → `HexToSpectreColor.ToMarkupColor()` → `DeterministicTypeColor` → ANSI-to-Spectre name mapping.

Both share:
- `TypeColorResolver` for user-override → ADO appearance → fallback resolution
- `IconSet.ResolveTypeBadge()` for badge glyph resolution
- `StateCategoryResolver` for state → category mapping

### 8.4 State Color Mapping Divergence (H-003)

This is the most architecturally significant rendering issue. The state-to-color mapping diverges between the two rendering paths:

```
HumanOutputFormatter.GetStateColor(state):
    StateCategoryResolver.Resolve(state, null)  ← always null entries
    └── Falls through to FallbackCategory() heuristic

SpectreTheme.GetStateStyle(state):
    StateCategoryResolver.Resolve(state, _stateEntries)  ← actual ADO entries
    └── Matches against ADO-defined categories first
```

**Consequence:** For custom states defined in ADO process templates (e.g., "Design", "Review"), `HumanOutputFormatter` uses the heuristic fallback (which may return `Unknown` → `Reset`), while `SpectreTheme` uses the ADO-authoritative category (which may correctly return `InProgress` → `Blue`). The same work item state renders different colors depending on which rendering path is active.

Additionally, `HumanOutputFormatter.GetStateColor` is a `static` method that cannot access instance state, making it structurally impossible to pass state entries without a larger refactor.

---

## 9. Gatekeeper Findings — Contextualized

### 9.1 HIGH Severity

#### H-001: Sync-over-async in RenderingServiceModule.cs

**Location:** `RenderingServiceModule.AddTwigRenderingServices()` → `SpectreTheme` factory
**Pattern:** `Task.Run(() => processTypeStore.GetAllAsync()).GetAwaiter().GetResult()`

**Architectural context:** This occurs in the DI composition root, which runs synchronously. The `Task.Run` wrapper prevents synchronization context deadlocks but wastes a thread pool thread. In a CLI (no SynchronizationContext), the `Task.Run` is technically unnecessary — the async method would complete on the thread pool anyway. The underlying issue is that `IProcessTypeStore.GetAllAsync()` is async but is called during synchronous container build.

**Risk:** Low in practice (CLI has no sync context), but it's an anti-pattern that could cause issues if the code were ever used in a hosted context (e.g., a test harness with a sync context).

**Recommended fix:** Accept `IReadOnlyList<StateEntry>?` as a parameter to `SpectreTheme` and compute it before the DI container is built (alongside the existing `TwigConfiguration` load), or switch to a `Lazy<SpectreTheme>` that initializes on first use.

#### H-002: Sync-over-async in PromptStateWriter

**Location:** `PromptStateWriter.WritePromptStateCore()`
**Pattern:** Three chained `.GetAwaiter().GetResult()` calls on `IContextStore`, `IWorkItemRepository`, and `IProcessTypeStore`.

**Architectural context:** `IPromptStateWriter.WritePromptState()` is a synchronous interface method called after mutating commands (state changes, note additions). The synchronous contract was chosen because prompt state writes must be fire-and-forget — they must not delay command output. The three async calls are to SQLite (via async ADO.NET), which in practice executes synchronously on a single connection.

**Risk:** Moderate. The bare `catch {}` at line 43-48 (M-006) compounds this — if `GetAwaiter().GetResult()` throws a synchronization exception, the `catch {}` swallows it silently.

**Recommended fix:** Change `WritePromptState()` to `WritePromptStateAsync()` and have callers `await` it (or fire-and-forget with `_ = WritePromptStateAsync()`). Alternatively, since the underlying SQLite operations are synchronous anyway, provide synchronous versions of the store methods.

#### H-003: State-to-color mapping duplicated 3× with correctness bug

**Location:** `HumanOutputFormatter.GetStateColor()`, `SpectreTheme.GetStateStyle()`, `SpectreTheme.FormatState()`
**Bug:** `HumanOutputFormatter` passes `null` for state entries, `SpectreTheme` passes actual entries.

**Architectural context:** This is a structural consequence of the dual rendering pipeline. `HumanOutputFormatter` was the original renderer; `SpectreTheme` was added later for progressive rendering. The formatter doesn't receive state entries because its `GetStateColor` is static and the formatter was constructed before process type data was readily available in the formatter's DI factory.

**Risk:** High for correctness. Custom ADO states will render with different colors in the sync vs. async path. Users may see different colors for the same state depending on whether output is piped or interactive.

**Recommended fix:** Extract a shared `StateColorMapper` service that resolves state entries once and is injected into both `HumanOutputFormatter` and `SpectreTheme`. Alternatively, pass `IReadOnlyList<StateEntry>?` to `HumanOutputFormatter`'s constructor (same as `SpectreTheme`).

### 9.2 MEDIUM Severity

#### M-001: Missing CancellationToken in ~28/33 non-alias commands

**Architectural context:** ConsoleAppFramework supports `CancellationToken` as a parameter — it automatically wires Ctrl+C. Only 5 of 35 commands (Set, Up, Down, Upgrade, Changelog) propagate it. This means most commands cannot be interrupted during ADO API calls.

**Risk:** Moderate UX impact. Users must wait for network timeouts or kill the process. For commands that perform multiple sequential API calls (refresh, flow-start, flow-done), this is more impactful.

#### M-002: Duplicated git repo check in 8+ commands

**Architectural context:** The guard `gitService is null || !IsInsideWorkTreeAsync()` is a prerequisite for git operations but is hand-coded in each command. This could be a filter/middleware.

#### M-003: Duplicated ActiveItemResult pattern-match in 14+ commands

**Architectural context:** The ActiveItemResult discriminated union is well-designed, but consumption requires the same exhaustive match in every command. An extension method on `ActiveItemResult` that returns `(WorkItem? item, int? exitCode)` or similar would collapse the 8-12 line block to 2-3 lines.

#### M-004: HumanOutputFormatter.GetStateColor ignores process-type-defined state entries

**Same root cause as H-003.** The static method cannot access instance state.

#### M-005: ConflictResolutionFlow treats unknown input as proceed-with-local

**Location:** `ConflictResolutionFlow.ResolveAsync()` line 74
**Pattern:** `// 'l' or any unrecognized input: proceed with local changes`

**Risk:** A user accidentally pressing Enter (empty input) or typing a typo proceeds with local changes, potentially losing remote updates. The safe default should be abort.

#### M-006: Bare `catch{}` in PromptStateWriter swallows OutOfMemoryException

**Location:** `PromptStateWriter.WritePromptState()` line 43-48
**Pattern:** `catch { /* Intentionally swallowed */ }`

**Risk:** Catches `OutOfMemoryException`, `StackOverflowException` (in theory), and other critical exceptions. Should use `catch (Exception)` at minimum, or better, `catch (Exception ex) when (ex is not OutOfMemoryException)`.

#### M-007: HookHandlerCommand regex lacks timeout

**Location:** `HookHandlerCommand.HandlePrepareCommitMsgAsync()` line 108, `HandleCommitMsgAsync()` line 146
**Pattern:** `Regex.IsMatch(content, ...)` without `RegexOptions.None, TimeSpan`

**Risk:** Low in practice (the patterns `#{id}(?!\d)` and `#\d+` are simple), but for defense-in-depth, a timeout prevents ReDoS from malformed commit messages.

#### M-008: ConfigCommand.SetValue doesn't validate enum-like values

**Location:** `TwigConfiguration.SetValue()` — the `auth.method` case accepts any string.
**Risk:** Users can set `auth.method=foo`, which will silently fail at runtime when the auth provider factory encounters an unknown method. Some paths (like `display.icons` and `flow.autoassign`) do validate; `auth.method` does not.

### 9.3 LOW Severity

#### L-001: Unused hintEngine injection in 6 commands

**Pattern:** `_ = hintEngine;` — The engine is injected but immediately discarded. Commands like `ConfigCommand`, and several git-focused commands don't currently use hints but receive the dependency.

**Assessment:** Minor DI hygiene issue. Not harmful but misleading.

#### L-002: ChangelogCommand doesn't use IOutputFormatter

**Pattern:** Direct `Console.WriteLine()` calls with manual formatting instead of using the injected formatter pattern.

**Assessment:** Inconsistency. If the changelog output were ever consumed by scripts, the lack of JSON/minimal formatters would be a gap.

#### L-003: CommandRegistrationModule is 282-line monolith

**Assessment:** This is a direct consequence of the AOT-safe factory lambda pattern. Each command requires an explicit factory with 5-14 `GetRequiredService` calls. Readability suffers. Could be mitigated by grouping registrations into sub-methods by category (context commands, git commands, flow commands, etc.).

#### L-004: Magic string "human" as default output format in 20+ signatures

**Assessment:** A constant `const string DefaultOutputFormat = "human"` would centralize the default. Currently, changing the default requires editing every signature in `TwigCommands` and every command's `ExecuteAsync`.

---

## 10. Architectural Risks and Technical Debt

### 10.1 Risk Matrix

| ID | Risk | Likelihood | Impact | Severity |
|----|------|-----------|--------|----------|
| R-1 | State color divergence causes user confusion | High | Medium | **High** |
| R-2 | Sync-over-async in DI causes test deadlocks | Medium | High | **High** |
| R-3 | Missing CancellationToken causes hung CLI | Medium | Medium | **Medium** |
| R-4 | Command boilerplate makes new commands error-prone | High | Low | **Medium** |
| R-5 | Bare catch{} masks critical failures | Low | High | **Medium** |
| R-6 | Conflict resolution defaults to proceed on bad input | Low | Medium | **Low** |
| R-7 | Regex without timeout in git hooks | Very Low | Medium | **Low** |
| R-8 | CommandRegistrationModule maintenance burden | High | Low | **Low** |

### 10.2 Technical Debt Hotspots

1. **Command layer duplication** — The combination of M-001, M-002, M-003, and L-004 means every new command requires ~20 lines of boilerplate before any business logic. This is the highest-volume debt.

2. **Dual rendering path divergence** — `HumanOutputFormatter` and `SpectreTheme` evolved independently and now have divergent state color resolution. As the theme system grows (more process templates, more custom states), this divergence will worsen.

3. **PromptStateWriter straddling layers** — Lives in Infrastructure but performs presentation-like concerns (badge rendering, color computation). Its synchronous interface forces sync-over-async. It's also the only infrastructure service that directly calls domain services (`StateCategoryResolver`, `TypeColorResolver`, `IconSet`).

4. **TUI sync-over-async** — `Twig.Tui` has `GetAwaiter().GetResult()` in several UI event handlers (`TreeNavigatorView`, `WorkItemFormView`, `Program.cs`). Terminal.Gui v2 doesn't currently support async dispatch, so this is framework-constrained. It should be revisited if Terminal.Gui v2 adds async support.

---

## 11. Prioritized Recommendations

### Priority 1 — Correctness (Fix Now)

| # | Recommendation | Addresses | Effort |
|---|----------------|-----------|--------|
| 1a | **Unify state color resolution** — Extract a `StateColorMapper` injected into both `HumanOutputFormatter` and `SpectreTheme`. Pass `IReadOnlyList<StateEntry>?` at construction time. | H-003, M-004 | Small |
| 1b | **Fix ConflictResolutionFlow default** — Change unknown input to abort instead of proceed-with-local. | M-005 | Trivial |
| 1c | **Narrow PromptStateWriter catch** — Replace bare `catch {}` with `catch (Exception) {}` at minimum. | M-006 | Trivial |

### Priority 2 — Robustness (Fix Soon)

| # | Recommendation | Addresses | Effort |
|---|----------------|-----------|--------|
| 2a | **Add CancellationToken to all commands** — Propagate through `TwigCommands` signatures and into `ExecuteAsync` methods. | M-001 | Medium |
| 2b | **Add regex timeout to HookHandlerCommand** — Use `Regex.IsMatch(content, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))`. | M-007 | Trivial |
| 2c | **Validate `auth.method` in SetValue** — Add `if (lower is "pat" or "azcli")` guard, consistent with `display.icons` and `flow.autoassign`. | M-008 | Trivial |

### Priority 3 — Maintainability (Plan for Next Cycle)

| # | Recommendation | Addresses | Effort |
|---|----------------|-----------|--------|
| 3a | **Extract `ActiveItemResult` resolution helper** — Add `ActiveItemResult.Resolve(result, formatter) → (WorkItem?, int?)` or an extension method. Reduces 8-12 line blocks to 2-3 lines across 19 commands. | M-003 | Small |
| 3b | **Extract git repo guard** — Create a shared `GitGuard.EnsureRepoAsync(gitService, formatter)` helper or ConsoleAppFilter. | M-002 | Small |
| 3c | **Define `DefaultOutputFormat` constant** — Replace magic string `"human"` in all signatures. | L-004 | Trivial |
| 3d | **Remove unused `hintEngine` injections** — Clean up `_ = hintEngine;` patterns. | L-001 | Trivial |
| 3e | **Add IOutputFormatter to ChangelogCommand** — Inject and use the formatter for consistency. | L-002 | Trivial |

### Priority 4 — Architecture Evolution (Backlog)

| # | Recommendation | Addresses | Effort |
|---|----------------|-----------|--------|
| 4a | **Refactor PromptStateWriter to async** — Change interface to `WritePromptStateAsync()`. Callers fire-and-forget. Eliminates sync-over-async. | H-002 | Medium |
| 4b | **Pre-compute SpectreTheme state entries before DI** — Load process types during bootstrap (alongside config), pass to `AddTwigRenderingServices`. Eliminates `Task.Run().GetAwaiter().GetResult()`. | H-001 | Medium |
| 4c | **Split CommandRegistrationModule** — Group registrations by category (context, git, flow, system) in separate static methods. | L-003 | Small |
| 4d | **Consider a command base class or pipeline** — Abstract `ActiveItemResult` handling, `OutputFormatterFactory` resolution, and `CancellationToken` propagation into a shared pipeline. | M-001–M-004 | Large |

---

## Appendix — Metrics and References

### A.1 Codebase Size

| Project | Source Files | Lines of Code |
|---------|-------------|---------------|
| Twig (CLI) | 51 | 7,872 |
| Twig.Domain | 67 | 2,781 |
| Twig.Infrastructure | 41 | 4,091 |
| Twig.Tui | 3 | 542 |
| **Total Source** | **162** | **15,286** |
| Tests (4 projects) | — | ~30,000 |

### A.2 Dependency Graph Summary

| From → To | Direct Dependencies |
|-----------|-------------------|
| Twig → Domain | Aggregates, Services, Interfaces, ValueObjects, ReadModels, Enums, Commands, Common |
| Twig → Infrastructure | Config, Ado, Git, GitHub, DI, Persistence (SqliteCacheStore only for init) |
| Infrastructure → Domain | Aggregates, Interfaces, ValueObjects, Services |
| Tui → Domain | Interfaces, Aggregates, ValueObjects |
| Tui → Infrastructure | Config, Persistence, DI (TwigServiceRegistration) |

### A.3 Key Technology Versions

| Technology | Version | Purpose |
|-----------|---------|---------|
| .NET | 10.0 | Runtime (Native AOT) |
| ConsoleAppFramework | 5.7.13 | CLI parsing, command dispatch |
| Spectre.Console | 0.54.0 | Progressive terminal rendering |
| Terminal.Gui | 2.0.0-develop.5185 | Full-screen TUI |
| Microsoft.Data.Sqlite | 9.0.14 | SQLite persistence |
| MinVer | 7.0.0 | Git-tag-based versioning |
| xUnit | 2.9.3 | Test framework |
| NSubstitute | 5.3.0 | Mocking |
| Shouldly | 4.3.0 | Assertion library |

### A.4 Gatekeeper Finding Cross-Reference

| Finding | Section | Priority |
|---------|---------|----------|
| H-001 Sync-over-async DI factory | §4.5, §9.1 | P4b |
| H-002 Sync-over-async PromptStateWriter | §9.1 | P4a |
| H-003 State color mapping divergence | §8.4, §9.1 | P1a |
| M-001 Missing CancellationToken | §5.3.3, §9.2 | P2a |
| M-002 Git repo check duplication | §5.3.2, §9.2 | P3b |
| M-003 ActiveItemResult duplication | §5.3.1, §9.2 | P3a |
| M-004 GetStateColor ignores entries | §8.4, §9.2 | P1a |
| M-005 Conflict default to proceed | §9.2 | P1b |
| M-006 Bare catch{} | §9.2 | P1c |
| M-007 Regex without timeout | §9.2 | P2b |
| M-008 No enum validation | §7.5, §9.2 | P2c |
| L-001 Unused hintEngine | §9.3 | P3d |
| L-002 ChangelogCommand no formatter | §9.3 | P3e |
| L-003 Registration monolith | §4.2, §9.3 | P4c |
| L-004 Magic "human" default | §5.3.4, §9.3 | P3c |
