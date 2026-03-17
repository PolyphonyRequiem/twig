# Twig Interactive & Async CLI UX — Umbrella Design & Implementation Plan

> **Revision**: 3 — Addresses technical review feedback (score: 78/100)  
> **Date**: 2026-03-15  
> **Status**: COMPLETED — All 8 EPICs implemented  
> **Author**: Architecture session  

---

## Executive Summary

This document defines the architecture and phased rollout plan for progressive, asynchronous terminal rendering and interactive TUI capabilities in the Twig CLI. Today, all Twig commands execute synchronously — they fetch data from ADO REST APIs, build a complete read model in memory, then render output in a single shot via `IOutputFormatter.Format*()` → `Console.WriteLine()`. Users see nothing during network fetches, which creates a perceived-slow experience on cold cache or large workspaces. This umbrella plan introduces a three-tier approach: **Tier 1** adds async progressive CLI output using Spectre.Console (spinners, live tables, streaming rows); **Tier 2** adds semi-interactive widgets (filterable selection prompts, expandable trees) still within the CLI flow; **Tier 3** delivers a full TUI mode using Terminal.Gui with Tree Navigator and Work Item Form Editor views per DI-004. The design preserves the existing `IOutputFormatter` contract as a sync fallback and introduces a parallel `IAsyncRenderer` pipeline that commands opt into. Each tier and each command scenario (workspace, tree, status, show, disambiguation) will have its own detailed sub-plan document.

> **Revision 2 Changes**: Corrected Spectre.Console AOT risk assessment based on documented `TypeConverterHelper` trim warnings (dotnet/runtime#115431); added mandatory AOT spike gating Tier 2 work. Revised `IAsyncRenderer.RenderWorkspaceAsync` signature to address the iteration-service sequencing gap. Added explicit `RenderHintsAsync` method to `IAsyncRenderer` for HintEngine integration. Documented Spectre.Console `Status`/`Live` mutual exclusivity and the required transition pattern. Updated Terminal.Gui v2 status to reflect beta release (March 2026). Replaced `exec` with `Process.Start` + exit code propagation for Windows compatibility. Corrected `twig tree` characterization (reads from SQLite, not ADO). Fixed NFR-IUX-002 measurement command for Windows. Removed `IContextStore` interface bloat in favor of existing KV store. Added `Spectre.Console.Testing` to dependency table. Added InvariantGlobalization analysis.
>
> **Revision 3 Changes**: Fixed `StreamWorkspaceData` scope error — changed from private method to **C# local function** with closure capture so `contextItem`, `sprintItems`, `seeds` are accessible in `ExecuteAsync`'s outer scope for `Workspace.Build()` and hint computation (Critical Issue 1). Redesigned EPIC-006 cache-then-refresh: stale-while-revalidate is now **command-owned** via extended `WorkspaceDataChunk` variants (`RefreshStarted`, `RefreshCompleted`) — the renderer never re-fetches data; the command yields additional chunks after detecting staleness (Critical Issue 2). Fixed NavigationCommands disambiguation: ITEM-025 now specifies calling `tree.FindByPattern(idOrPattern)` directly instead of `tree.MoveDown(idOrPattern)` because `MoveDown` discards `multi.Candidates` in its `Result.Fail<int>()` return (Critical Issue 3). Updated `Table.Caption` code sketch to use fluent extension method `table.Caption(new TableTitle(...))` for consistency with documented Spectre.Console patterns — note: the property setter IS valid per API docs, but fluent form is preferred (Non-critical Issue 1). Clarified FR-IUX-002: "progressive" in Tier 1 means **stage-by-stage** (context → sprint batch → seeds), not individual row streaming — true row-by-row requires optional `IStreamingWorkItemSource` (Non-critical Issue 2).

---

## Background

### Current Architecture

Twig is a .NET 9 Native AOT CLI built on ConsoleAppFramework v5.7.13. The solution follows a clean three-layer architecture:

| Layer | Project | Responsibility |
|-------|---------|----------------|
| CLI | `src/Twig` | Commands, formatters, hints, DI composition |
| Domain | `src/Twig.Domain` | Aggregates (`WorkItem`), read models (`Workspace`, `WorkTree`), domain interfaces, value objects |
| Infrastructure | `src/Twig.Infrastructure` | ADO REST client, SQLite persistence, authentication, config |

**Formatter Pipeline (current)**:  
Commands follow a uniform pattern:
1. Resolve the `IOutputFormatter` via `OutputFormatterFactory.GetFormatter(format)`
2. Fetch data from repositories/services (all `await`-based, but fully sequential)
3. Build a domain read model (`Workspace`, `WorkTree`, etc.)
4. Call `fmt.Format*(readModel)` → returns a complete `string`
5. `Console.WriteLine(result)`
6. Optionally render hints via `HintEngine`

Three formatter implementations exist:
- **`HumanOutputFormatter`** — ANSI-colored, box-drawing, badges, manual `StringBuilder`
- **`JsonOutputFormatter`** — Stable JSON schema, manual `Utf8JsonWriter` (AOT-safe)
- **`MinimalOutputFormatter`** — Single-line, no ANSI, pipe-friendly

**Key Constraint**: The `IOutputFormatter` interface returns `string` from every method. This is fundamentally synchronous — the entire output must be materialized before it can be written. There is no streaming or incremental path.

**Data Flow (example: `twig workspace`)**:
```
WorkspaceCommand.ExecuteAsync()
  → contextStore.GetActiveWorkItemIdAsync()       // SQLite read
  → workItemRepo.GetByIdAsync(activeId)            // SQLite read
  → iterationService.GetCurrentIterationAsync()    // ADO REST call (slow)
  → workItemRepo.GetByIterationAsync(iteration)    // SQLite read
  → workItemRepo.GetSeedsAsync()                   // SQLite read
  → Workspace.Build(context, sprintItems, seeds)   // In-memory
  → fmt.FormatWorkspace(workspace, staleDays)       // String materialization
  → Console.WriteLine(result)                       // Output
```

The ADO REST call at step 3 is the bottleneck — it can take 500ms–2s on cold cache. Steps 2, 4, and 5 are local SQLite reads (< 10ms each).

### Context and Motivation

- **DI-004** (requirements doc): Pre-decided that Terminal.Gui is the TUI framework, with Vim-style keybindings and Tree Navigator + Work Item Form Editor views
- **RD-019**: Disambiguation currently renders a static numbered list; no interactive selection
- **RD-021**: TUI is post-MVP — this plan implements that post-MVP phase
- **NFR-001**: Read operations from local cache MUST complete in < 200ms — progressive rendering must not regress this

### Prior Art in Codebase

- `RefreshCommand` already shows incremental `Console.WriteLine` messages ("Refreshing from ADO...", "Iteration: ...", "Refreshed N item(s)") — a primitive form of progressive output
- `SetCommand` prints "Fetching work item {id} from ADO..." before the network call — an ad-hoc spinner substitute
- The codebase uses manual ANSI escape codes throughout `HumanOutputFormatter` — no rendering library dependency exists today

---

## Problem Statement

1. **Perceived latency**: Commands like `twig workspace` and `twig refresh` make ADO REST calls that take 500ms–2s. Users see a blank terminal until all data is fetched and formatted. There is no spinner, progress indicator, or partial output. (Note: `twig tree` reads entirely from SQLite cache — `GetChildrenAsync`, `GetByIdAsync`, `GetParentChainAsync` — with no ADO calls, so its perceived latency is already low at < 50ms. Progressive rendering for tree is a visual enhancement, not a latency fix.)

2. **All-or-nothing rendering**: The `IOutputFormatter` contract returns a complete `string`. There is no mechanism to render a table header immediately, then populate rows as data arrives. Column widths cannot adjust dynamically.

3. **No interactive disambiguation**: When `twig set <pattern>` matches multiple items, it prints a static numbered list and exits with error code 1 (see `SetCommand.cs:50-55`). The user must re-run the command with a more specific pattern or numeric ID. Per FR-016/RD-019, interactive disambiguation is desired.

4. **No TUI mode**: DI-004 specifies Tree Navigator and Work Item Form Editor views, but no implementation path exists. The question of whether a lightweight "semi-interactive" mode (filterable prompts, expandable trees) can serve some TUI use cases without Terminal.Gui has not been evaluated.

5. **Data fetching is fully sequential**: `WorkspaceCommand` awaits each data source in sequence. There is no parallel fetching, no `IAsyncEnumerable` streaming, and no cache-then-refresh pattern.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|----|------|---------|
| G-001 | Users see output within 100ms of command invocation | Time-to-first-pixel measured for workspace/tree/status commands |
| G-002 | Progressive rendering for all human-format read commands | Spinner shown during fetch; data rendered incrementally as it arrives |
| G-003 | Interactive selection prompt for disambiguation | Filterable list shown when pattern matches multiple items |
| G-004 | Cache-then-refresh pattern | Show cached data immediately, refresh in background, indicate staleness |
| G-005 | Full TUI mode with tree navigation and work item editing | Terminal.Gui-based views per DI-004 |
| G-006 | Maintain AOT compatibility | All rendering libraries must be AOT-compatible with `PublishAot=true` |
| G-007 | Backward-compatible — existing sync formatters continue to work unchanged | JSON and Minimal formatters are unaffected; `--output json` remains synchronous |

### Non-Goals

- **NG-001**: Rewriting the existing `IOutputFormatter` interface or its three implementations — they remain the sync path
- **NG-002**: Adding Spectre.Console.Cli as a command-line parser — ConsoleAppFramework is retained
- **NG-003**: Real-time collaboration or multi-user TUI — single-user only per existing scope
- **NG-004**: Streaming output for JSON/minimal formats — these remain synchronous, batch-output formats
- **NG-005**: Mobile or web terminal support — standard terminal emulators only

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-IUX-001 | Commands MUST show a spinner/status indicator within 100ms when a network call is in progress | High |
| FR-IUX-002 | `twig workspace` MUST render workspace data progressively **stage-by-stage** (context item → sprint item batch → seeds) as each data-fetching stage completes, with a live-updating table. **Note**: "progressive" in Tier 1 means stage-by-stage, not individual row streaming — `IWorkItemRepository.GetByIterationAsync()` returns `IReadOnlyList<WorkItem>` (a complete batch). True row-by-row streaming requires the optional `IStreamingWorkItemSource` extension (Section 4). | High |
| FR-IUX-003 | `twig tree` MUST render the parent chain and focused item immediately from cache, then progressively load and display children. **Note**: TreeCommand reads entirely from SQLite (zero ADO calls), so this is a visual enhancement rather than a latency fix. | Medium |
| FR-IUX-004 | `twig status` MUST render a dashboard-style view with panels for current item, pending changes, and hints | Medium |
| FR-IUX-005 | When disambiguation produces multiple matches, `twig set` MUST show an interactive filterable selection prompt (human format only) | High |
| FR-IUX-006 | A `--no-live` flag MUST disable progressive rendering and fall back to the existing sync formatter pipeline | Medium |
| FR-IUX-007 | The cache layer MUST support a stale-while-revalidate pattern: serve cached data immediately, fetch fresh data in background, and display a "cached (stale)" badge while refreshing | Medium |
| FR-IUX-008 | TUI mode (`twig tui` or `twig --tui`) MUST launch a full-screen Terminal.Gui application with Tree Navigator and Work Item Form Editor views | Low (Tier 3) |
| FR-IUX-009 | TUI Tree Navigator MUST support Vim-style keybindings (j/k for up/down, Enter to expand/select, q to quit) | Low (Tier 3) |
| FR-IUX-010 | Progressive rendering MUST NOT degrade performance for local-only reads (< 200ms per NFR-001) | High |

### Non-Functional Requirements

| ID | Requirement | Metric |
|----|-------------|--------|
| NFR-IUX-001 | All rendering libraries MUST be AOT-compatible | `dotnet publish` with `PublishAot=true` succeeds with no trim warnings |
| NFR-IUX-002 | Spectre.Console dependency MUST NOT increase published binary size by more than 2 MB | Measured via `(Get-Item .\twig.exe).Length` of AOT-published binary before/after |
| NFR-IUX-003 | Progressive rendering MUST degrade gracefully on non-TTY outputs (e.g., piped to file) | Detect `Console.IsOutputRedirected`; fall back to sync path |
| NFR-IUX-004 | Terminal.Gui MUST be an optional dependency, not required for non-TUI usage | Separate project or lazy-loaded assembly |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                         CLI Commands                            │
│  WorkspaceCommand  TreeCommand  StatusCommand  SetCommand  ...  │
└──────────┬──────────────────────────────┬───────────────────────┘
           │                              │
     ┌─────▼─────┐                 ┌──────▼──────┐
     │  Sync Path │                 │  Async Path  │
     │ (existing) │                 │    (new)      │
     └─────┬─────┘                 └──────┬──────┘
           │                              │
  ┌────────▼────────┐          ┌──────────▼──────────┐
  │ IOutputFormatter │          │ IAsyncRenderer       │
  │ (string return)  │          │ (void, writes to     │
  │                  │          │  IAnsiConsole)        │
  ├──────────────────┤          ├──────────────────────┤
  │ HumanOutput...   │          │ SpectreRenderer      │
  │ JsonOutput...    │          │  ├─ LiveTable         │
  │ MinimalOutput... │          │  ├─ LiveTree          │
  └──────────────────┘          │  ├─ StatusSpinner     │
                                │  ├─ SelectionPrompt   │
                                │  └─ DashboardLayout   │
                                ├──────────────────────┤
                                │ TerminalGuiRenderer   │
                                │  (Tier 3 — separate   │
                                │   project)            │
                                └──────────────────────┘
```

**Key Design Decision**: The async rendering path is **parallel** to the existing sync path, not a replacement. Commands select the path based on: (1) output format — only `human` uses async rendering; (2) TTY detection — redirected output falls back to sync; (3) explicit `--no-live` flag.

### Key Components

#### 1. `IAsyncRenderer` Interface

New interface introduced in `src/Twig/Rendering/IAsyncRenderer.cs`:

```csharp
namespace Twig.Rendering;

/// <summary>
/// Async rendering pipeline for progressive CLI output.
/// Implementations write directly to the terminal (via IAnsiConsole or similar).
/// Unlike IOutputFormatter, methods are void — rendering is a side effect, not a return value.
/// </summary>
public interface IAsyncRenderer
{
    /// <summary>
    /// Render a workspace with progressive table population.
    /// The getWorkspaceData delegate encapsulates the full data-fetching sequence
    /// (context resolution, iteration fetch, sprint item query, seed query) and
    /// yields partial results as each stage completes. This design avoids exposing
    /// the iteration→sprintItems sequencing dependency to the renderer while still
    /// enabling the renderer to show progress between stages.
    /// </summary>
    Task RenderWorkspaceAsync(
        IAsyncEnumerable<WorkspaceDataChunk> getWorkspaceData,
        int staleDays,
        CancellationToken ct);

    /// <summary>Render a work item tree with progressive child loading.</summary>
    Task RenderTreeAsync(
        Func<Task<WorkItem?>> getFocusedItem,
        Func<Task<IReadOnlyList<WorkItem>>> getParentChain,
        Func<Task<IReadOnlyList<WorkItem>>> getChildren,
        int maxChildren,
        int? activeId,
        CancellationToken ct);

    /// <summary>Render a status dashboard with live panels.</summary>
    Task RenderStatusAsync(
        Func<Task<WorkItem?>> getItem,
        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
        CancellationToken ct);

    /// <summary>Render a work item detail view with progressive field loading.</summary>
    Task RenderWorkItemAsync(
        Func<Task<WorkItem?>> getItem,
        bool showDirty,
        CancellationToken ct);

    /// <summary>
    /// Show an interactive selection prompt for disambiguation.
    /// Returns the selected (Id, Title) or null if cancelled.
    /// </summary>
    Task<(int Id, string Title)?> PromptDisambiguationAsync(
        IReadOnlyList<(int Id, string Title)> matches,
        CancellationToken ct);

    /// <summary>
    /// Render contextual hints after main command output.
    /// Integrates with HintEngine output in the async rendering pipeline.
    /// Called by commands after the primary render method completes.
    /// </summary>
    void RenderHints(IReadOnlyList<string> hints);
}

/// <summary>
/// Represents a chunk of workspace data yielded during progressive loading.
/// Each variant corresponds to a stage in the workspace data-fetching pipeline.
/// This pattern solves the iteration→sprintItems sequencing problem: the command
/// orchestrates the fetch order and yields results as they complete; the renderer
/// processes each chunk and updates the display progressively.
///
/// The RefreshStarted/RefreshCompleted variants support the stale-while-revalidate
/// pattern (EPIC-006). After initial data is rendered, if the cache is stale, the
/// command yields RefreshStarted (renderer shows "⟳ refreshing..." badge), re-fetches
/// data from ADO, yields updated data chunks, then yields RefreshCompleted (renderer
/// removes the badge). This keeps the renderer infrastructure-agnostic — it never
/// calls ADO services directly; the command owns all data fetching.
/// </summary>
public abstract record WorkspaceDataChunk
{
    /// <summary>Active context item loaded from SQLite cache.</summary>
    public sealed record ContextLoaded(WorkItem? ContextItem) : WorkspaceDataChunk;

    /// <summary>Sprint items loaded (requires iteration to have been resolved first).</summary>
    public sealed record SprintItemsLoaded(IReadOnlyList<WorkItem> Items) : WorkspaceDataChunk;

    /// <summary>Seed items loaded from SQLite cache.</summary>
    public sealed record SeedsLoaded(IReadOnlyList<WorkItem> Seeds) : WorkspaceDataChunk;

    /// <summary>
    /// Signals the renderer that a background refresh has started because the cache
    /// is stale. The renderer SHOULD show a visual indicator (e.g., "⟳ refreshing..."
    /// badge). The command layer emits this before re-fetching from ADO.
    /// </summary>
    public sealed record RefreshStarted : WorkspaceDataChunk;

    /// <summary>
    /// Signals the renderer that the background refresh is complete. The renderer
    /// SHOULD remove the stale/refreshing indicator. Emitted after updated data
    /// chunks have been yielded.
    /// </summary>
    public sealed record RefreshCompleted : WorkspaceDataChunk;
}
```

**Design Rationale — Workspace Data Sequencing (addresses Critical Finding 2)**:

The original design used three independent `Func<Task<T>>` delegates for workspace rendering: `getContext`, `getSprintItems`, and `getSeeds`. This was problematic because `WorkspaceCommand` has a sequential dependency: `iterationService.GetCurrentIterationAsync()` must complete before `workItemRepo.GetByIterationAsync(iteration)` can be called. A simple `Func<Task<IReadOnlyList<WorkItem>>>` delegate cannot cleanly express this two-step dependency — either the iteration must be pre-resolved (making the "renderer controls timing" rationale hollow) or the delegate must internally await both calls (hiding the latency boundary from the renderer).

The revised design uses `IAsyncEnumerable<WorkspaceDataChunk>`. The command owns the fetch orchestration and yields typed chunks as each stage completes. The renderer consumes chunks and updates the display progressively. This cleanly separates concerns:
- **Command**: knows the data-fetching sequence and dependencies
- **Renderer**: knows how to display each chunk type and update the UI

```csharp
// In WorkspaceCommand.ExecuteAsync() — StreamWorkspaceData is a LOCAL FUNCTION
// (not a private method) so it captures outer-scope variables via closure.
// This is required because contextItem, sprintItems, and seeds must be
// accessible after the stream is consumed for Workspace.Build() and hint computation.

WorkItem? contextItem = null;
IReadOnlyList<WorkItem> sprintItems = Array.Empty<WorkItem>();
IReadOnlyList<WorkItem> seeds = Array.Empty<WorkItem>();

async IAsyncEnumerable<WorkspaceDataChunk> StreamWorkspaceData(
    [EnumeratorCancellation] CancellationToken ct)
{
    var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
    contextItem = activeId.HasValue
        ? await workItemRepo.GetByIdAsync(activeId.Value, ct) : null;
    yield return new WorkspaceDataChunk.ContextLoaded(contextItem);

    // Sequential dependency: iteration must resolve before sprint items
    var iteration = await iterationService.GetCurrentIterationAsync(ct);
    sprintItems = await workItemRepo.GetByIterationAsync(iteration, ct);
    yield return new WorkspaceDataChunk.SprintItemsLoaded(sprintItems);

    seeds = await workItemRepo.GetSeedsAsync(ct);
    yield return new WorkspaceDataChunk.SeedsLoaded(seeds);
}
```

**⚠ Critical Implementation Detail — Local Function, Not Private Method**:

`StreamWorkspaceData` MUST be a **C# local function** defined inside `ExecuteAsync`, NOT a private method on `WorkspaceCommand`. This is because `contextItem`, `sprintItems`, and `seeds` are declared as variables in `ExecuteAsync`'s scope and assigned via closure capture inside the local function. After `RenderWorkspaceAsync` consumes the stream, these variables hold the fetched data and are used to build the `Workspace` for hint computation:

```csharp
// ExecuteAsync body — after stream is consumed, closured variables are populated
await renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), staleDays, ct);

// contextItem, sprintItems, seeds are now populated via closure side-effects
var workspace = Workspace.Build(contextItem, sprintItems, seeds);
var hints = hintEngine.GetHints("workspace", workspace: workspace, outputFormat: outputFormat);
renderer.RenderHints(hints);
```

If `StreamWorkspaceData` were a private method (as originally stated in ITEM-010 Rev 2), these variables would be local to the method and inaccessible in `ExecuteAsync`'s scope — the code would not compile.

**Design Rationale — HintEngine Integration (addresses Critical Finding 3)**:

All four affected commands (`WorkspaceCommand`, `TreeCommand`, `StatusCommand`, `SetCommand`) render hints after main output via `hintEngine.GetHints()`. The `IAsyncRenderer.RenderHints(IReadOnlyList<string> hints)` method provides an explicit integration point. Commands call `hintEngine.GetHints(...)` after the primary render method completes, then pass the results to `RenderHints()`. This keeps hint generation in the command (where the context is available) and hint rendering in the renderer (where the terminal is controlled). The `RenderHints` method is synchronous because hints are always computed from already-available data and rendered after the main display is finalized.

```csharp
// In WorkspaceCommand.ExecuteAsync() — after primary render
// StreamWorkspaceData is a local function that captures contextItem, sprintItems, seeds
await renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), staleDays, ct);

// After stream consumption, closured variables are populated
var workspace = Workspace.Build(contextItem, sprintItems, seeds);
var hints = hintEngine.GetHints("workspace", workspace: workspace, outputFormat: outputFormat);
renderer.RenderHints(hints);
```

#### 2. `SpectreRenderer` (Tier 1 + Tier 2)

Implements `IAsyncRenderer` using Spectre.Console v0.54 (latest stable). Key Spectre.Console features used:

| Feature | Use Case |
|---------|----------|
| `AnsiConsole.Live(table)` | Progressive table rendering for workspace/sprint items (with inline spinner via `Spinner` column) |
| `Table` | Formatted workspace and work item tables |
| `Tree` | Hierarchical tree rendering with expand/collapse |
| `SelectionPrompt<T>` | Interactive disambiguation (Tier 2) — **requires AOT spike validation** |
| `Layout` + `Panel` | Dashboard-style status view |
| `Rule` | Section dividers |

**⚠ AOT Risk Assessment (addresses Critical Finding 1)**:

Spectre.Console's AOT compatibility is **partially verified, not fully proven**. The core rendering features (`Table`, `Tree`, `Panel`, `Rule`, `Live`) use concrete types and are expected to be AOT-safe. However:

- **Known issue**: `dotnet/runtime#115431` (May 2025) documents that `Spectre.Console.TypeConverterHelper.FuncWithDam` produces `IL2067` trim analysis errors when used with `dotnet publish` on .NET 9. This was hit by the Aspire CLI team.
- **Affected code path**: `SelectionPrompt<T>` (used for Tier 2 disambiguation) relies on `TypeConverterHelper` internally for type conversion. This is the precise code path that triggers the trim warning.
- **Project constraint**: `Directory.Build.props` sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, meaning any ILLink trim warning becomes a build error. The published binary will fail to build if any Spectre.Console code path produces a trim warning.
- **Risk level**: **Medium-High** for `SelectionPrompt<T>` specifically; **Low** for core rendering widgets (`Table`, `Tree`, `Panel`, `Live`).

**Mitigation**: EPIC-001 includes a mandatory AOT spike (ITEM-001A) that must validate each Spectre.Console widget type used in the plan under the project's exact AOT configuration (`PublishAot=true`, `TreatWarningsAsErrors=true`, `InvariantGlobalization=true`, `TrimMode=full`). Tier 2 work (EPIC-005, `SelectionPrompt<T>`) is **gated** on the spike confirming no trim warnings for prompts. If `SelectionPrompt<T>` fails AOT validation, the fallback is a custom prompt implementation using `AnsiConsole.Live()` with keyboard input — see Alternatives Considered.

**InvariantGlobalization Consideration**: The project sets `<InvariantGlobalization>true</InvariantGlobalization>` in `Twig.csproj`. This disables culture-specific string behavior, which could affect Spectre.Console's Unicode box-drawing character detection (`Profile.Capabilities.Unicode`). Spectre.Console auto-detects Unicode support via `Console.OutputEncoding` — under InvariantGlobalization, string comparisons and character classification may behave differently. The AOT spike (ITEM-001A) MUST verify that table borders and box-drawing characters render correctly under InvariantGlobalization.

**Spectre.Console `Status`/`Live` Mutual Exclusivity (addresses Critical Finding 4)**:

Spectre.Console's `Status()` and `Live()` contexts are **mutually exclusive** — they cannot be open simultaneously or nested. Both capture the console's rendering pipeline exclusively. The Spectre.Console docs explicitly state: "Status display is not thread safe. Using it together with other interactive components such as prompts, progress displays, or other status displays is not supported." The same constraint applies to `Live()`.

For workspace progressive rendering, the required pattern is:

```csharp
// CORRECT: Sequential Status → Live transition
public async Task RenderWorkspaceAsync(
    IAsyncEnumerable<WorkspaceDataChunk> data, int staleDays, CancellationToken ct)
{
    // Phase 1: Spinner during initial data resolution
    // Use Live with a spinner renderable instead of Status,
    // so we can transition to table without switching contexts.
    var table = new Table();
    table.AddColumn("ID").AddColumn("Type").AddColumn("Title").AddColumn("State");

    await _console.Live(table)
        .StartAsync(async ctx =>
        {
            // Initially show empty table with "Loading..." row
            table.AddRow("[dim]Loading workspace...[/]", "", "", "");
            ctx.Refresh();

            await foreach (var chunk in data.WithCancellation(ct))
            {
                switch (chunk)
                {
                    case WorkspaceDataChunk.ContextLoaded(var contextItem):
                        // Render context panel above table
                        // Note: Table.Caption has both a { get; set; } property AND
                        // a fluent extension method. Using the fluent form for consistency
                        // with documented Spectre.Console patterns.
                        table.Caption(new TableTitle(
                            contextItem is not null
                                ? $"Active: #{contextItem.Id} {contextItem.Title}"
                                : "[dim]No active context[/]"));
                        table.Rows.Clear();
                        ctx.Refresh();
                        break;

                    case WorkspaceDataChunk.SprintItemsLoaded(var items):
                        // Populate table rows progressively
                        foreach (var item in items)
                        {
                            table.AddRow(
                                item.Id.ToString(),
                                SpectreTheme.FormatTypeBadge(item.Type),
                                Markup.Escape(item.Title),
                                SpectreTheme.FormatState(item.State));
                        }
                        ctx.Refresh();
                        break;

                    case WorkspaceDataChunk.SeedsLoaded(var seeds):
                        // Add seeds section
                        // ...
                        ctx.Refresh();
                        break;

                    case WorkspaceDataChunk.RefreshStarted:
                        // Show stale-while-revalidate badge
                        table.Caption(new TableTitle("[yellow]⟳ refreshing...[/]"));
                        ctx.Refresh();
                        break;

                    case WorkspaceDataChunk.RefreshCompleted:
                        // Remove refresh badge, show fresh indicator
                        table.Caption(new TableTitle("[green]✓ up to date[/]"));
                        ctx.Refresh();
                        break;
                }
            }
        });
}
```

**Key insight**: Rather than transitioning from `Status()` to `Live()` (which requires stopping one context and starting another), the design uses a single `Live()` context throughout. The initial "loading" state is represented as a styled row in the live table itself (e.g., `[dim]Loading workspace...[/]`) rather than a separate `Status()` spinner. This avoids the mutual exclusivity problem entirely while still providing visual loading feedback. If a standalone spinner is desired before any table structure exists, it can be rendered as a `Live()` context with a `Rows.Panel("[spinner] Loading...")` renderable, then replaced via `ctx.UpdateTarget(table)` when the first data chunk arrives.

#### 3. `RenderingPipelineFactory`

Replaces direct `OutputFormatterFactory` usage in commands. Decides which path to use:

```csharp
namespace Twig.Rendering;

public sealed class RenderingPipelineFactory(
    OutputFormatterFactory formatterFactory,
    IAsyncRenderer asyncRenderer)
{
    /// <summary>
    /// Returns (formatter, asyncRenderer) pair. asyncRenderer is null when
    /// sync path should be used (non-human format, redirected output, --no-live).
    /// </summary>
    public (IOutputFormatter Formatter, IAsyncRenderer? Renderer) Resolve(
        string outputFormat, bool noLive = false)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Async path only for human format, TTY output, and no --no-live flag
        if (string.Equals(outputFormat, "human", StringComparison.OrdinalIgnoreCase)
            && !Console.IsOutputRedirected
            && !noLive)
        {
            return (fmt, asyncRenderer);
        }

        return (fmt, null);
    }
}
```

#### 4. Streaming Data Layer (`IAsyncEnumerable` Extensions)

New extension methods on `IWorkItemRepository` for streaming results:

```csharp
namespace Twig.Domain.Interfaces;

/// <summary>
/// Extension interface for streaming work item data.
/// Implementations can yield items as they are read from SQLite (row-by-row)
/// or fetched from ADO (batch-by-batch).
/// </summary>
public interface IStreamingWorkItemSource
{
    IAsyncEnumerable<WorkItem> StreamByIterationAsync(
        IterationPath iterationPath, CancellationToken ct = default);

    IAsyncEnumerable<WorkItem> StreamChildrenAsync(
        int parentId, CancellationToken ct = default);
}
```

This is optional for Tier 1 — the initial implementation can use the existing `IWorkItemRepository` methods wrapped in `Func<Task<IReadOnlyList<T>>>` delegates. Streaming becomes valuable when fetching large result sets from ADO.

#### 5. Cache-Then-Refresh Pattern

For commands that read from cache, the **command layer** owns the entire stale-while-revalidate flow (see DD-011). The renderer never re-fetches data — it only processes `WorkspaceDataChunk` variants:

```
1. Command reads from SQLite cache (< 10ms)
2. Command yields initial data chunks → renderer renders immediately
3. Command checks cache freshness via contextStore.GetValueAsync("last_refreshed_at")
4. If stale (older than cacheStaleMinutes):
   a. yield RefreshStarted → renderer shows "⟳ refreshing..." badge
   b. Command fetches fresh data from ADO
   c. yield updated SprintItemsLoaded/SeedsLoaded → renderer replaces table rows
   d. Command updates last_refreshed_at timestamp
   e. yield RefreshCompleted → renderer removes badge
5. If fresh: stream ends after step 2 — no refresh needed
```

All data fetching remains in the command layer. The `SpectreRenderer` handles `RefreshStarted`/`RefreshCompleted` as display-only signals within the existing `Live()` context.

#### 6. Terminal.Gui Integration (Tier 3)

Per DI-004, Terminal.Gui is the TUI framework. Terminal.Gui v2 **reached beta in March 2026** (see [blog announcement](https://blog.kindel.com/2026/03/05/terminal-gui-still-absurd-now-beta/)) with significant architectural improvements: instance-based `IApplication` (replaces static singleton), `Prompt<TView, TResult>` API, rewritten mouse/input system with virtual time testing, and proper `IDisposable` lifecycle. The v2 beta API is described as "stable" but breaking changes before GA remain possible.

**Terminal.Gui does NOT currently support Native AOT** (see [GitHub issue #3109](https://github.com/gui-cs/Terminal.Gui/issues/3109) and [discussion #2414](https://github.com/gui-cs/Terminal.Gui/discussions/2414)). The library uses reflection internally, and trimming/AOT causes runtime failures.

**Proposed Mitigation**: The TUI mode will be in a separate project (`src/Twig.Tui`) that is compiled without AOT. The main `twig` binary remains AOT; invoking `twig tui` either:
- (Option A) Launches a separate `twig-tui` executable (non-AOT) — cleanest separation
- (Option B) The main binary includes Terminal.Gui but disables AOT when TUI is present — breaks NFR-IUX-001

**Recommendation**: Option A — separate executable. The `twig` command detects `tui` subcommand and launches `twig-tui` via `Process.Start()` + `WaitForExit()` + `Environment.Exit(process.ExitCode)`. This preserves AOT for all other commands and correctly propagates exit codes on Windows (there is no `exec()` syscall on Windows).

### Data Flow (Revised — Progressive Workspace)

```
WorkspaceCommand.ExecuteAsync()
  → pipeline.Resolve("human")
  → if asyncRenderer is not null:
      → Declare contextItem, sprintItems, seeds in ExecuteAsync scope
      → Define StreamWorkspaceData as LOCAL FUNCTION (closure captures outer vars)
      → renderer.RenderWorkspaceAsync(
            StreamWorkspaceData(ct),   // IAsyncEnumerable<WorkspaceDataChunk>
            staleDays)
      → StreamWorkspaceData internally yields chunks (assigns captured vars):
          1. yield ContextLoaded    — contextStore + workItemRepo (~10ms, SQLite)
          2. yield SprintItemsLoaded — iterationService (~500ms-2s, ADO REST)
                                       then workItemRepo.GetByIteration (~10ms)
          3. yield SeedsLoaded      — workItemRepo.GetSeedsAsync (~10ms, SQLite)
      → SpectreRenderer consumes IAsyncEnumerable:
          1. Receive ContextLoaded → show table with Active context immediately
          2. Receive SprintItemsLoaded → populate sprint item rows
          3. Receive SeedsLoaded → render Seeds section
          4. Done — final render
      → contextItem, sprintItems, seeds now populated via closure side-effects
      → Workspace.Build(contextItem, sprintItems, seeds)
      → hintEngine.GetHints("workspace", workspace: workspace, ...)
      → renderer.RenderHints(hints)
  → else (sync path):
      → existing code unchanged
```

**Note on `twig tree` data flow**: Unlike `twig workspace`, the `TreeCommand` makes zero ADO REST calls — it reads entirely from SQLite cache (`GetByIdAsync`, `GetParentChainAsync`, `GetChildrenAsync`). All three calls complete in < 10ms each. Progressive rendering for `twig tree` therefore provides a **visual enhancement** (structured tree display with Spectre.Console's `Tree` widget) rather than a latency reduction. The `RenderTreeAsync` interface retains `Func<Task<T>>` delegates because tree data has no sequential dependencies between stages.

### API Contracts

#### Command-level Changes

Commands gain an optional `bool noLive = false` parameter (wired via `--no-live` flag). The command body becomes:

```csharp
// Before
var fmt = formatterFactory.GetFormatter(outputFormat);
// ... fetch all data ...
Console.WriteLine(fmt.FormatWorkspace(workspace, staleDays));

// After — StreamWorkspaceData is a LOCAL FUNCTION with closure capture
var (fmt, renderer) = pipelineFactory.Resolve(outputFormat, noLive);
if (renderer is not null)
{
    // Variables declared in ExecuteAsync scope — captured by closure in StreamWorkspaceData
    WorkItem? contextItem = null;
    IReadOnlyList<WorkItem> sprintItems = Array.Empty<WorkItem>();
    IReadOnlyList<WorkItem> seeds = Array.Empty<WorkItem>();

    // Local function — NOT a private method. Captures and assigns outer-scope variables.
    async IAsyncEnumerable<WorkspaceDataChunk> StreamWorkspaceData(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
        contextItem = activeId.HasValue
            ? await workItemRepo.GetByIdAsync(activeId.Value, ct) : null;
        yield return new WorkspaceDataChunk.ContextLoaded(contextItem);

        var iteration = await iterationService.GetCurrentIterationAsync(ct);
        sprintItems = await workItemRepo.GetByIterationAsync(iteration, ct);
        yield return new WorkspaceDataChunk.SprintItemsLoaded(sprintItems);

        seeds = await workItemRepo.GetSeedsAsync(ct);
        yield return new WorkspaceDataChunk.SeedsLoaded(seeds);
    }

    await renderer.RenderWorkspaceAsync(
        StreamWorkspaceData(ct),
        staleDays, ct);

    // After stream is consumed, closured variables are populated
    var workspace = Workspace.Build(contextItem, sprintItems, seeds);
    var hints = hintEngine.GetHints("workspace",
        workspace: workspace, outputFormat: outputFormat);
    renderer.RenderHints(hints);
}
else
{
    // Existing sync path unchanged
    // ... fetch all data sequentially ...
    Console.WriteLine(fmt.FormatWorkspace(workspace, staleDays));
    // ... existing hint rendering unchanged ...
}
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-001 | Spectre.Console v0.54 for Tier 1 + Tier 2 rendering, **contingent on AOT spike** | MIT licensed, rich widget library (Live, Table, Tree, Prompts, Status), actively maintained. However, AOT compatibility is **partially validated, not fully proven** — `TypeConverterHelper` produces IL2067 trim warnings on .NET 9 (dotnet/runtime#115431). Core rendering widgets (Table, Tree, Panel, Live) are expected to be safe; `SelectionPrompt<T>` is higher risk. ITEM-001A is a mandatory spike that gates Tier 2 adoption. |
| DD-002 | Parallel rendering pipeline (not replacement) | Preserves existing sync formatters unchanged. JSON/minimal output unaffected. Zero risk to existing behavior. |
| DD-003 | `IAsyncEnumerable<WorkspaceDataChunk>` for workspace data streaming | Solves the iteration→sprintItems sequencing problem. Commands own fetch orchestration and yield typed chunks; renderer consumes chunks and updates display. Avoids the unsolvable `Func<Task<T>>` delegate composition problem for multi-step dependencies. Other render methods (tree, status) retain `Func<Task<T>>` delegates where data has no stage dependencies. `WorkspaceDataChunk` also includes `RefreshStarted`/`RefreshCompleted` variants for stale-while-revalidate (DD-011). |
| DD-004 | Terminal.Gui in separate executable, launched via `Process.Start` | Terminal.Gui lacks AOT support. Separate binary preserves AOT for the primary CLI. Windows has no `exec()` syscall — the handoff uses `Process.Start()` + `WaitForExit()` + `Environment.Exit(process.ExitCode)` for exit code propagation. |
| DD-005 | `--no-live` flag for opt-out | Power users, CI environments, and compatibility scenarios can disable progressive rendering. |
| DD-006 | Spectre.Console as a dependency of `src/Twig` only | Domain and Infrastructure layers remain rendering-agnostic. Only the CLI project takes the dependency. |
| DD-007 | Cache-then-refresh is opt-in per command | Not all commands benefit from stale-while-revalidate. `twig status` and `twig workspace` opt in; `twig set` does not. |
| DD-008 | `RenderHints` as explicit method on `IAsyncRenderer` | HintEngine integration must be explicit in the async path. Commands compute hints after primary render (using already-fetched data), then call `RenderHints()`. This keeps hint generation in the command layer and hint rendering in the display layer. |
| DD-009 | Single `Live()` context instead of `Status()` → `Live()` transition | Spectre.Console's `Status()` and `Live()` contexts are mutually exclusive. Using a single `Live()` context throughout avoids the transition problem. Loading state is shown as a styled renderable within the `Live()` context. |
| DD-010 | Cache freshness tracking via existing `IContextStore.GetValueAsync`/`SetValueAsync` | The existing KV store on `IContextStore` supports arbitrary string keys. Adding `last_refreshed_at` as a key-value entry avoids interface bloat. No new methods required on `IContextStore`. |
| DD-011 | Stale-while-revalidate owned by command layer, not renderer | The renderer accepts `IAsyncEnumerable<WorkspaceDataChunk>` — a one-time stream. After the stream is consumed, the renderer has no mechanism to re-fetch data and should not depend on infrastructure (ADO services). The command layer owns staleness detection and re-fetch: after yielding initial data chunks, it checks `last_refreshed_at` freshness; if stale, it yields `RefreshStarted` (renderer shows badge), re-fetches from ADO, yields updated `SprintItemsLoaded`/`SeedsLoaded` chunks (renderer replaces table rows), then yields `RefreshCompleted` (renderer removes badge). This preserves layer separation — the renderer remains infrastructure-agnostic. |
| DD-012 | `NavigationCommands.DownAsync` uses `tree.FindByPattern()` for disambiguation, not `tree.MoveDown()` | `WorkTree.MoveDown()` returns `Result.Fail<int>(errorMessage)` on multi-match — the `multi.Candidates` list is discarded in the error string. `WorkTree.FindByPattern()` returns `MatchResult.MultipleMatches { Candidates }`, which provides the candidate list needed for `PromptDisambiguationAsync`. The command calls `FindByPattern` directly and handles all three match outcomes. |

---

## Alternatives Considered

### Rendering Engine

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Spectre.Console** | Rich widgets, MIT, 9k+ GitHub stars, active maintenance | Adds ~1-2 MB to binary, AOT partially validated (TypeConverterHelper issue), InvariantGlobalization untested | **Selected conditionally** — Tier 1 (core rendering) proceeds; Tier 2 (SelectionPrompt) gated on AOT spike |
| **Manual ANSI escape codes** (current approach) | Zero dependencies, full control, minimal binary size | Must reimplement spinners, live tables, trees, prompts from scratch. Significant effort. | Rejected — too much effort to build and maintain |
| **Humanizer + custom rendering** | Humanizer is AOT-friendly | Humanizer is for text formatting, not terminal UI. Still need spinners/live display. | Rejected — wrong tool for the job |
| **Gui.cs / Terminal.Gui for all tiers** | Single framework for everything | No AOT support, heavy dependency, full-screen TUI not appropriate for quick CLI commands | Rejected for Tier 1/2 — retained for Tier 3 only |

### Async Interface Design

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **`IAsyncEnumerable<WorkspaceDataChunk>` for workspace** | Command owns fetch orchestration, solves iteration→sprintItems sequencing, renderer processes chunks as they arrive | More complex type hierarchy (discriminated union pattern) | **Selected** — only clean solution for multi-step data dependencies |
| **`IAsyncRenderer` with `Func<Task<T>>` delegates (original)** | Renderer controls timing, commands stay simple | Cannot capture iteration→sprintItems dependency in a single delegate; breaks "renderer controls timing" for the primary scenario | Rejected for workspace — **retained for tree/status/show** where data has no sequential dependencies |
| **`IAsyncOutputFormatter` returning `IAsyncEnumerable<string>`** | Symmetric with existing `IOutputFormatter` | Doesn't allow live updates (re-rendering in place), just streaming lines | Rejected — too limited for live tables and dashboards |
| **Extend `IOutputFormatter` with optional async methods** | Single interface | Violates ISP; forces async awareness on JSON/minimal formatters | Rejected — pollutes the existing clean interface |

### TUI Deployment Model

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Separate `twig-tui` executable via `Process.Start` (Option A)** | Main binary stays AOT, clean separation, works on Windows | Two binaries to distribute, process handoff complexity | **Selected** — preserves AOT constraint, platform-correct |
| **Single binary with Terminal.Gui (Option B)** | Single binary distribution | Breaks AOT entirely for all commands | Rejected — unacceptable regression for CLI performance |
| **Spectre.Console Layout as TUI substitute** | No Terminal.Gui needed, single binary | Limited interactivity — no persistent input loop, no form editing | Evaluated — useful for Tier 2 but insufficient for Tier 3 |

### SelectionPrompt AOT Fallback (if spike fails)

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Custom prompt using `AnsiConsole.Live()` + keyboard** | Full AOT compatibility, no TypeConverterHelper dependency | Must implement keyboard handling, selection state, filtering manually | **Contingency** — used if ITEM-001A spike shows SelectionPrompt produces trim warnings |
| **`System.Console.ReadKey` + manual ANSI rendering** | Zero Spectre.Console dependency for prompts | Reimplements what Spectre.Console provides; inconsistent styling | Rejected — too low-level |

---

## Dependencies

### External Dependencies

| Dependency | Version | License | AOT Status | Purpose |
|------------|---------|---------|------------|---------|
| Spectre.Console | 0.54.0 | MIT | ⚠ Partially compatible — core rendering OK, `SelectionPrompt<T>` has known IL2067 trim warnings (dotnet/runtime#115431). Requires spike validation. | Tier 1 + Tier 2 rendering |
| Spectre.Console.Testing | 0.54.0 | MIT | ✅ Compatible (test-only, not published) | Test doubles for `IAnsiConsole` — required for `SpectreRenderer` unit tests (ITEM-007, ITEM-012) |
| Terminal.Gui | 2.0.0-beta.* (latest beta) | MIT | ❌ Not compatible | Tier 3 TUI (separate non-AOT binary) |
| ConsoleAppFramework | 5.7.13 (existing) | MIT | ✅ Compatible | Command routing (unchanged) |

> **Note on Terminal.Gui v2 maturity (addresses Critical Finding 5)**: As of March 2026, Terminal.Gui v2 has reached **beta** status (see [blog announcement](https://blog.kindel.com/2026/03/05/terminal-gui-still-absurd-now-beta/)). The beta includes significant architectural improvements: instance-based `IApplication` (replaces static singleton), `Prompt<TView, TResult>` API, and rewritten mouse/input system. The API is described as "stable" in the beta announcement, but breaking changes before GA remain possible. Earlier NuGet versions (`2.0.0-prealpha.1834`) were pre-alpha — the beta is a meaningful maturity upgrade. Tier 3 timeline should target the beta or GA release.

### Internal Dependencies

- `IOutputFormatter` interface and all three implementations — must remain unchanged
- `OutputFormatterFactory` — used by `RenderingPipelineFactory`
- `IWorkItemRepository` and all existing domain interfaces — consumed by async rendering delegates
- `HintEngine` — integrated via `IAsyncRenderer.RenderHints()` method; commands compute hints after primary render and pass to renderer
- `IContextStore.GetValueAsync`/`SetValueAsync` — used for cache freshness tracking (`last_refreshed_at` key), no new interface methods needed

### Sequencing Constraints

1. Tier 1 (async progressive rendering) must be completed before Tier 2 (interactive prompts)
2. Tier 2 can proceed independently of Tier 3
3. Tier 3 requires Terminal.Gui AOT evaluation and separate project setup

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `src/Twig/Twig.csproj` | New PackageReference: Spectre.Console |
| `Directory.Packages.props` | New PackageVersion entries |
| `src/Twig/Program.cs` | DI registration for `IAsyncRenderer`, `RenderingPipelineFactory` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Refactored to use `RenderingPipelineFactory`, async rendering path |
| `src/Twig/Commands/TreeCommand.cs` | Same refactoring pattern |
| `src/Twig/Commands/StatusCommand.cs` | Same refactoring pattern |
| `src/Twig/Commands/SetCommand.cs` | Disambiguation path uses `PromptDisambiguationAsync` |
| `src/Twig/Commands/NavigationCommands.cs` | Down command disambiguation uses interactive prompt |
| `src/Twig/Formatters/*` | **Unchanged** — sync path preserved |

### Backward Compatibility

- `--output json` and `--output minimal` are completely unaffected
- `--output human` gains progressive rendering but `--no-live` reverts to current behavior
- Exit codes and error messages unchanged
- Piped output (`twig workspace | grep`) auto-detects non-TTY and falls back to sync

### Performance Implications

- **Positive**: Time-to-first-pixel drops from 500ms–2s to < 100ms for network-bound commands
- **Neutral**: Local-only reads (< 200ms) may see a small overhead from Spectre.Console's `AnsiConsole` initialization (~10ms)
- **Binary size**: Spectre.Console adds ~1-2 MB to the AOT-published binary (currently ~15 MB)

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Spectre.Console core rendering widgets (`Table`, `Tree`, `Panel`, `Live`) produce AOT trim warnings with `PublishAot=true` | Low | High | ITEM-001A spike validates each widget type under exact project AOT config. If any core widget fails, evaluate pinning to a specific Spectre.Console version or contributing upstream fixes. |
| Spectre.Console `SelectionPrompt<T>` produces AOT trim warnings via `TypeConverterHelper` (documented in dotnet/runtime#115431) | **Medium-High** | High | ITEM-001A spike must specifically validate `SelectionPrompt<T>`. If it fails: (a) implement custom prompt using `AnsiConsole.Live()` + keyboard input, or (b) suppress the specific warning with `[UnconditionalSuppressMessage]` if runtime behavior is verified safe, or (c) use string-only prompt to avoid TypeConverterHelper code path. |
| Spectre.Console rendering under `InvariantGlobalization=true` produces incorrect box-drawing or Unicode characters | Medium | Medium | ITEM-001A spike includes visual verification of table borders and box-drawing under InvariantGlobalization. May need explicit `Console.OutputEncoding = Encoding.UTF8` before Spectre.Console initialization. |
| Terminal.Gui v2 beta API changes before GA break Tier 3 implementation | Medium | Medium | Tier 3 uses separate non-AOT binary. Pin Terminal.Gui version. Defer Tier 3 implementation until Terminal.Gui v2 reaches RC or GA if beta is too unstable. |
| Spectre.Console `Live` display conflicts with ConsoleAppFramework's stdout handling | Low | Medium | ConsoleAppFramework writes to stdout via `Console.WriteLine`; Spectre.Console captures the console. Ensure the async path fully owns the console during rendering. |
| Progressive rendering causes visual artifacts on some terminal emulators | Medium | Low | Test on Windows Terminal, iTerm2, and common Linux terminals. Provide `--no-live` fallback. |
| Binary size increase from Spectre.Console exceeds 2 MB | Low | Low | Measure and report. Consider trimming unused Spectre.Console features if needed. |
| `IAsyncEnumerable<WorkspaceDataChunk>` pattern increases command complexity | Low | Low | Provide a helper method or base class that commands can use to construct the async enumerable from their fetch logic. Document the pattern in developer guide. |

---

## Open Questions

| ID | Question | Owner | Status |
|----|----------|-------|--------|
| OQ-001 | Should `twig show <id>` be a new command or an extension of `twig status`? Currently `twig show` is an alias for `twig workspace`. | Product | **Resolved** — New command `twig show <id>` providing extended detail view of a single work item, complementary to `twig status`. Unbind `show` from `workspace` alias. |
| OQ-002 | What is the cache staleness threshold for the stale-while-revalidate pattern? 5 minutes? Configurable? | Product | **Resolved** — Default 5 minutes, configurable via config key (e.g. `cache.staleMinutes`). |
| OQ-003 | Should Tier 2 interactive selection prompts be enabled by default, or opt-in via `--interactive`? | Product | **Resolved** — Interactive by default when human usage is implied (TTY detected, human output format). Non-interactive when tool integration is likely (piped/redirected output, JSON/minimal format, non-TTY stdin). No flag needed; `--no-interactive` escape hatch for scripts running in a TTY that want deterministic behavior. |
| OQ-004 | Should the `twig-tui` separate binary share the same `.twig/` directory and SQLite database? | Architecture | **Resolved** — Yes, shared `.twig/` directory and SQLite database. |
| OQ-005 | Terminal.Gui v2 reached beta in March 2026. Should Tier 3 target the beta, wait for RC/GA, or proceed with the understanding that some API churn is expected? | Architecture | **Resolved** — Target Terminal.Gui v2 beta, accept API churn risk since Tier 3 is last. |
| OQ-006 | Should Spectre.Console's `AnsiConsole` be injected via DI, or should we use the static `AnsiConsole` instance? | Architecture | **Resolved** — DI via `IAnsiConsole` for testability with `Spectre.Console.Testing`. |
| OQ-007 | If `SelectionPrompt<T>` fails the AOT spike, should we implement a custom filterable prompt or fall back to the existing static disambiguation list? | Architecture | **Contingent** on EPIC-001 AOT spike (ITEM-001A) results. Decision deferred until spike completes. |

---

## Implementation Phases

### Phase 1: Foundation (Tier 1 Core)

**Exit Criteria**: Spectre.Console integrated, `IAsyncRenderer` interface defined, `SpectreRenderer` skeleton implemented, one command (`twig workspace`) uses progressive rendering with spinner. AOT publish verified.

### Phase 2: Progressive Commands (Tier 1 Complete)

**Exit Criteria**: `twig workspace`, `twig tree`, `twig status` all use progressive rendering. `--no-live` fallback works. Non-TTY detection works. All existing tests pass.

### Phase 3: Interactive Widgets (Tier 2)

**Exit Criteria**: `twig set <pattern>` shows interactive selection prompt on multiple matches. Tree view supports expand/collapse via keyboard. Filterable workspace list.

### Phase 4: Cache-Then-Refresh

**Exit Criteria**: `twig workspace` and `twig status` show cached data immediately with "stale" badge, refresh in background, update display.

### Phase 5: Full TUI (Tier 3)

**Exit Criteria**: `twig-tui` binary launches full-screen Terminal.Gui app with Tree Navigator and Work Item Form Editor. Vim-style keybindings work.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Rendering/IAsyncRenderer.cs` | Async rendering interface and `WorkspaceDataChunk` discriminated union |
| `src/Twig/Rendering/SpectreRenderer.cs` | Spectre.Console implementation of `IAsyncRenderer` |
| `src/Twig/Rendering/RenderingPipelineFactory.cs` | Route selection between sync and async paths |
| `src/Twig/Rendering/SpectreTheme.cs` | Centralized Spectre.Console color/style mappings matching existing ANSI theme |
| `tests/Twig.Cli.Tests/Rendering/SpectreRendererTests.cs` | Tests for async rendering |
| `tests/Twig.Cli.Tests/Rendering/RenderingPipelineFactoryTests.cs` | Tests for path selection logic |
| `docs/projects/twig-iux-workspace.plan.md` | Sub-plan: progressive workspace rendering |
| `docs/projects/twig-iux-tree.plan.md` | Sub-plan: progressive tree rendering |
| `docs/projects/twig-iux-status.plan.md` | Sub-plan: progressive status dashboard |
| `docs/projects/twig-iux-show.plan.md` | Sub-plan: progressive detail view |
| `docs/projects/twig-iux-disambiguation.plan.md` | Sub-plan: interactive selection prompts |
| `docs/projects/twig-iux-tui.plan.md` | Sub-plan: Terminal.Gui full TUI |
| `src/Twig.Tui/Twig.Tui.csproj` | Tier 3: Terminal.Gui TUI project (non-AOT) |
| `src/Twig.Tui/Program.cs` | Tier 3: TUI entry point |
| `src/Twig.Tui/Views/TreeNavigatorView.cs` | Tier 3: Tree navigator view |
| `src/Twig.Tui/Views/WorkItemFormView.cs` | Tier 3: Work item form editor |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `Directory.Packages.props` | Add `Spectre.Console` and `Spectre.Console.Testing` PackageVersion entries |
| `src/Twig/Twig.csproj` | Add `Spectre.Console` PackageReference |
| `tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj` | Add `Spectre.Console.Testing` PackageReference (test doubles for `IAnsiConsole`) |
| `src/Twig/Program.cs` | Register `IAsyncRenderer`, `SpectreRenderer`, `RenderingPipelineFactory` in DI |
| `src/Twig/Commands/WorkspaceCommand.cs` | Add async rendering path alongside existing sync path; add `StreamWorkspaceData` method |
| `src/Twig/Commands/TreeCommand.cs` | Add async rendering path alongside existing sync path |
| `src/Twig/Commands/StatusCommand.cs` | Add async rendering path alongside existing sync path |
| `src/Twig/Commands/SetCommand.cs` | Use interactive prompt for disambiguation (human format only) |
| `src/Twig/Commands/NavigationCommands.cs` | Use interactive prompt for down-command disambiguation |
| `Twig.slnx` | Add `Twig.Tui` project (Tier 3) |

### Deleted Files

None.

---

## Implementation Plan

### EPIC-001: Foundation — Spectre.Console Integration & Async Rendering Infrastructure

**Status**: DONE ✅  
**Completed**: 2026-03-16

**Goal**: Integrate Spectre.Console, define the `IAsyncRenderer` interface (including `WorkspaceDataChunk` types and `RenderHints`), implement `SpectreRenderer` skeleton, wire DI, and **validate AOT compatibility with a mandatory spike**.

**Prerequisites**: None

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add Spectre.Console v0.54.0 and Spectre.Console.Testing v0.54.0 to `Directory.Packages.props`. Add `Spectre.Console` PackageReference to `src/Twig/Twig.csproj`. Add `Spectre.Console.Testing` PackageReference to `tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj`. | `Directory.Packages.props`, `src/Twig/Twig.csproj`, `tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj` | DONE |
| ITEM-001A | IMPL | **MANDATORY AOT SPIKE (gates all subsequent work)**: Create a minimal spike branch that exercises every Spectre.Console widget type used in this plan (`Table`, `Tree`, `Panel`, `Rule`, `Live`, `Status`, `SelectionPrompt<string>`, `Layout`) under the project's exact AOT configuration: `PublishAot=true`, `TreatWarningsAsErrors=true`, `InvariantGlobalization=true`, `TrimMode=full`. Run `dotnet publish -r win-x64 -c Release`. Document: (a) which widgets produce zero trim warnings, (b) which produce IL2067 or other ILLink warnings, (c) whether box-drawing/Unicode table borders render correctly under InvariantGlobalization. **If `SelectionPrompt<T>` fails**: document and flag EPIC-005 for alternative implementation. **If core widgets fail**: escalate — Spectre.Console may not be viable. | Spike branch (not merged) | DONE |
| ITEM-002 | IMPL | Create `IAsyncRenderer` interface in `src/Twig/Rendering/IAsyncRenderer.cs` with: `RenderWorkspaceAsync` (accepts `IAsyncEnumerable<WorkspaceDataChunk>`), `RenderTreeAsync`, `RenderStatusAsync`, `RenderWorkItemAsync`, `PromptDisambiguationAsync`, and `RenderHints`. Also define `WorkspaceDataChunk` discriminated union (abstract record with `ContextLoaded`, `SprintItemsLoaded`, `SeedsLoaded`, `RefreshStarted`, `RefreshCompleted` variants). | `src/Twig/Rendering/IAsyncRenderer.cs` | DONE |
| ITEM-003 | IMPL | Create `SpectreTheme.cs` mapping existing ANSI color constants and type badges from `HumanOutputFormatter` to Spectre.Console `Style` objects. Ensure visual parity. | `src/Twig/Rendering/SpectreTheme.cs` | DONE |
| ITEM-004 | IMPL | Create `RenderingPipelineFactory` that resolves sync vs. async path based on output format, TTY detection, and `--no-live` flag. | `src/Twig/Rendering/RenderingPipelineFactory.cs` | DONE |
| ITEM-005 | IMPL | Create `SpectreRenderer` class implementing `IAsyncRenderer`. Initial implementation: `RenderWorkspaceAsync` using single `Live()` context pattern (no Status→Live transition — see DD-009). Show loading state as styled row, process `WorkspaceDataChunk` variants progressively including `RefreshStarted`/`RefreshCompleted` for stale-while-revalidate badge (see DD-011). `RenderHints` renders hint strings below main output. Other methods throw `NotImplementedException`. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-006 | IMPL | Register `IAnsiConsole` (DI), `IAsyncRenderer` → `SpectreRenderer`, and `RenderingPipelineFactory` in DI in `Program.cs`. Inject `IAnsiConsole` via `AnsiConsole.Console` (allows test substitution). | `src/Twig/Program.cs` | DONE |
| ITEM-007 | TEST | Create unit tests for `RenderingPipelineFactory`: verify sync path for JSON/minimal, async path for human TTY, sync fallback for redirected output and `--no-live`. Use `Spectre.Console.Testing.TestConsole` for `IAnsiConsole` test doubles. | `tests/Twig.Cli.Tests/Rendering/RenderingPipelineFactoryTests.cs` | DONE |
| ITEM-008 | TEST | Create AOT smoke test: publish AOT binary with Spectre.Console dependency, run `twig workspace --no-live`, verify no runtime errors. This validates the production AOT pipeline end-to-end (separate from ITEM-001A spike which tests individual widgets). | CI/manual verification | DONE |

**Acceptance Criteria**:
- [x] ITEM-001A spike completed: all widget AOT/InvariantGlobalization results documented
- [x] Core rendering widgets (`Table`, `Tree`, `Panel`, `Rule`, `Live`) confirmed AOT-clean or workaround identified
- [x] `SelectionPrompt<T>` AOT status determined — EPIC-005 gated accordingly (`SelectionPrompt<T>` not referenced; EPIC-005 flagged for alternative if needed)
- [x] `dotnet publish -c Release` with `PublishAot=true` succeeds with Spectre.Console dependency
- [x] `RenderingPipelineFactory` correctly routes to sync/async based on format, TTY, and `--no-live`
- [x] `SpectreTheme` colors match existing `HumanOutputFormatter` ANSI colors visually
- [x] `IAsyncRenderer.RenderHints` method implemented in `SpectreRenderer`
- [x] All existing tests continue to pass

**Reviewer Notes (follow-up for EPIC-002+)**:
- Issue 1 (Medium): `SeedsLoaded` case in `RenderWorkspaceAsync` silently drops seeds data — add `// TODO (EPIC-002): render seeds below table` comment
- Issue 2 (Low): `SpectreTheme.GetStateStyle` is dead code — only `FormatState` is used; consider removing or extracting shared switch
- Issue 3 (Low): `Spectre.Console.Testing` package referenced but unused — add `SpectreRenderer` unit tests or defer package reference
- Issue 4 (Low): `RenderingPipelineFactory` DI registration is intentional scaffolding — add `// Injected by WorkspaceCommand in EPIC-002` comment

---

### EPIC-002: Progressive Workspace Rendering — DONE ✅

**Goal**: `twig workspace` shows a spinner during data fetch and renders sprint items, seeds, and context progressively using Spectre.Console Live Table.

**Prerequisites**: EPIC-001

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-009 | IMPL | Implement `SpectreRenderer.RenderWorkspaceAsync`: consume `IAsyncEnumerable<WorkspaceDataChunk>` within a single `Live()` context (see DD-009 — no Status→Live transition). Process chunks: `ContextLoaded` → set table caption/header via fluent `table.Caption(new TableTitle(...))` method; `SprintItemsLoaded` → add rows; `SeedsLoaded` → add seeds section; `RefreshStarted` → show "⟳ refreshing..." badge; `RefreshCompleted` → remove badge. Show loading state as a styled `[dim]Loading workspace...[/]` row that is cleared when first data chunk arrives. Call `ctx.Refresh()` after each chunk. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-010 | IMPL | Refactor `WorkspaceCommand.ExecuteAsync` to use `RenderingPipelineFactory`. If async renderer available: (a) declare `contextItem`, `sprintItems`, `seeds` as variables in `ExecuteAsync` scope; (b) implement `StreamWorkspaceData` as a **C# local function** (not a private method) that captures these variables via closure and assigns them as each data stage completes; (c) call `renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), staleDays, ct)`; (d) after stream consumption, build `Workspace` from the closure-populated variables for hint computation; (e) call `renderer.RenderHints(hintEngine.GetHints(...))`. **IMPORTANT**: `StreamWorkspaceData` must be a local function because the outer `ExecuteAsync` scope needs access to the fetched data after the stream is consumed. A private method would scope these variables locally and make them inaccessible. Else use existing sync path unchanged. | `src/Twig/Commands/WorkspaceCommand.cs` | DONE |
| ITEM-011 | IMPL | Add `--no-live` parameter to `TwigCommands.Workspace()`, `Show()`, `Ws()` method signatures and pass through to `WorkspaceCommand`. | `src/Twig/Program.cs` | DONE |
| ITEM-012 | TEST | Test `WorkspaceCommand` with async renderer: verify table is populated from `WorkspaceDataChunk` stream, hints rendered via `RenderHints`. Use `Spectre.Console.Testing.TestConsole` as `IAnsiConsole` test double. | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandAsyncTests.cs` | DONE |
| ITEM-013 | TEST | Verify `twig workspace --output json` still produces identical JSON output (regression test). | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | DONE |
| ITEM-014 | IMPL | Create sub-plan document `docs/projects/twig-iux-workspace.plan.md` with detailed UX specs (column widths, badge placement, spinner text, color mapping). | `docs/projects/twig-iux-workspace.plan.md` | DONE |

**Acceptance Criteria**:
- [x] `twig workspace` shows spinner within 100ms of invocation
- [x] Sprint items appear in a formatted table as they are loaded
- [x] `twig workspace --output json` output is byte-identical to pre-change output
- [x] `twig workspace --no-live` uses the original sync rendering path
- [x] Piped output (`twig workspace | cat`) auto-falls back to sync path

---

### EPIC-003: Progressive Tree Rendering — DONE ✅

**Status**: DONE ✅  
**Completed**: 2026-03-16

**Goal**: `twig tree` renders parent chain and focused item immediately, then progressively loads and displays children. **Note**: `TreeCommand` reads entirely from SQLite cache (zero ADO calls — see `TreeCommand.cs` which calls only `GetByIdAsync`, `GetParentChainAsync`, `GetChildrenAsync`). Progressive rendering here is a **visual enhancement** (structured Spectre.Console `Tree` widget vs. manual box-drawing) rather than a latency reduction.

**Prerequisites**: EPIC-001

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-015 | IMPL | Implement `SpectreRenderer.RenderTreeAsync`: render parent chain with dimmed Spectre `Tree` nodes, focused item with bold markup, then progressively add children as they load. Use Spectre.Console `Tree` widget. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-016 | IMPL | Refactor `TreeCommand.ExecuteAsync` to use `RenderingPipelineFactory` with async path for human format. | `src/Twig/Commands/TreeCommand.cs` | DONE |
| ITEM-017 | TEST | Test tree rendering with mock data: verify parent chain rendering, focused item highlight, child progressive loading. | `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | DONE |
| ITEM-018 | IMPL | Create sub-plan document `docs/projects/twig-iux-tree.plan.md`. | `docs/projects/twig-iux-tree.plan.md` | DONE |

**Acceptance Criteria**:
- [x] `twig tree` shows parent chain immediately (from cache)
- [x] Children appear progressively as they are loaded
- [x] Visual output is equivalent to current `HumanOutputFormatter.FormatTree` but with Spectre.Console styling
- [x] `--output json` and `--output minimal` unchanged

---

### EPIC-004: Progressive Status Dashboard — DONE ✅

**Status**: DONE ✅  
**Completed**: 2026-03-16

**Goal**: `twig status` renders a dashboard-style layout with panels for current item, pending changes, and contextual hints.

**Prerequisites**: EPIC-001

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-019 | IMPL | Implement `SpectreRenderer.RenderStatusAsync`: use Spectre.Console `Panel` and `Grid` to render work item details and pending change summary as a dashboard layout. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-020 | IMPL | Refactor `StatusCommand.ExecuteAsync` to use `RenderingPipelineFactory`. After primary render, call `renderer.RenderHints(hintEngine.GetHints("status", ...))` to integrate HintEngine output. | `src/Twig/Commands/StatusCommand.cs` | DONE |
| ITEM-021 | TEST | Test status rendering with dirty item, pending notes, and stale seeds. | `tests/Twig.Cli.Tests/Commands/StatusCommandAsyncTests.cs` | DONE |
| ITEM-022 | IMPL | Create sub-plan document `docs/projects/twig-iux-status.plan.md`. | `docs/projects/twig-iux-status.plan.md` | DONE |

**Acceptance Criteria**:
- [x] `twig status` shows a panel-based dashboard in human format
- [x] Pending changes count and dirty indicator visible
- [x] Hints rendered within the dashboard layout

---

### EPIC-005: Interactive Disambiguation Prompts (Tier 2) — DONE ✅

**Status**: DONE ✅  
**Completed**: 2026-03-16

**Goal**: When `twig set <pattern>` matches multiple items, show a filterable selection prompt instead of a static list.

**Prerequisites**: EPIC-001 — **specifically, ITEM-001A spike must confirm `SelectionPrompt<T>` is AOT-clean**. If the spike shows trim warnings, this EPIC must use the custom `Live()`-based prompt fallback instead (see Alternatives Considered → "SelectionPrompt AOT Fallback").

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-023 | IMPL | Implement `SpectreRenderer.PromptDisambiguationAsync`: custom `AnsiConsole.Live()`-based prompt (AOT-safe fallback — `SelectionPrompt<T>` produces IL2067 trim warnings). Support keyboard navigation and filtering. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-024 | IMPL | Refactor `SetCommand.ExecuteAsync`: when multiple matches found and async renderer available, call `PromptDisambiguationAsync` instead of printing static list. If user selects an item, continue with that selection. | `src/Twig/Commands/SetCommand.cs` | DONE |
| ITEM-025 | IMPL | Refactor `NavigationCommands.DownAsync`: instead of calling `tree.MoveDown(idOrPattern)` (which discards `multi.Candidates` in its `Result.Fail<int>()` return), call `tree.FindByPattern(idOrPattern)` directly. `FindByPattern` returns `MatchResult` which exposes `MatchResult.MultipleMatches { Candidates }` — the candidate list needed for interactive disambiguation. Handle all three outcomes: `SingleMatch` → call `setCommand.ExecuteAsync(id.ToString())`; `MultipleMatches` → if async renderer available, call `renderer.PromptDisambiguationAsync(multi.Candidates)` and proceed with selected item; `NoMatch` → print error. This mirrors the `SetCommand` disambiguation pattern but operates on the children of the current work tree node. See DD-012 for rationale. | `src/Twig/Commands/NavigationCommands.cs` | DONE |
| ITEM-026 | TEST | Test disambiguation: verify selection prompt shown for multiple matches, correct item selected, JSON format still returns static list. | `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` | DONE |
| ITEM-027 | IMPL | Create sub-plan document `docs/projects/twig-iux-disambiguation.plan.md`. | `docs/projects/twig-iux-disambiguation.plan.md` | DONE |

**Acceptance Criteria**:
- [x] `twig set "auth"` with 3 matches shows an interactive selection prompt
- [x] User can filter by typing and select with Enter
- [x] Selected item becomes the active context (same behavior as `twig set <id>`)
- [x] `twig set "auth" --output json` returns the static match list (unchanged)
- [x] Non-TTY output falls back to static list with exit code 1

---

### EPIC-006: Cache-Then-Refresh Pattern

**Goal**: Read commands show cached data immediately with a "stale" indicator, then refresh in background and update the display. **Architecture**: The command layer owns the entire stale-while-revalidate flow — the renderer never re-fetches data. The command extends its `IAsyncEnumerable<WorkspaceDataChunk>` stream: after yielding initial cached data, it checks freshness; if stale, it yields `RefreshStarted`, re-fetches from ADO, yields updated data chunks, then yields `RefreshCompleted`. The renderer processes these chunk types without any knowledge of ADO services. See DD-011 for rationale.

**Prerequisites**: EPIC-002 (workspace rendering must be progressive)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-028 | IMPL | Implement cache freshness tracking using the existing `IContextStore.GetValueAsync`/`SetValueAsync` KV store. Store `last_refreshed_at` as a string-serialized UTC timestamp via `contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"))`. Read via `contextStore.GetValueAsync("last_refreshed_at")` and parse. No new interface methods required (see DD-010). | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Commands/StatusCommand.cs` | DONE |
| ITEM-029 | IMPL | Add `display.cacheStaleMinutes` config property (default: 5). | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-030 | IMPL | Extend `WorkspaceCommand.StreamWorkspaceData` local function to support stale-while-revalidate: after yielding initial `ContextLoaded`, `SprintItemsLoaded`, `SeedsLoaded` chunks, check `last_refreshed_at` freshness via `contextStore.GetValueAsync("last_refreshed_at")`. If stale (older than `cacheStaleMinutes`): yield `RefreshStarted` (renderer shows "⟳ refreshing..." badge), call `iterationService.GetCurrentIterationAsync()` + `workItemRepo.GetByIterationAsync()` to re-fetch sprint items from ADO, yield updated `SprintItemsLoaded` chunk (renderer replaces table rows), call `workItemRepo.GetSeedsAsync()` for fresh seeds, yield updated `SeedsLoaded`, update `last_refreshed_at` timestamp, then yield `RefreshCompleted` (renderer removes badge). The `SpectreRenderer` handles `RefreshStarted`/`RefreshCompleted` chunks by adding/removing a badge and clearing/re-populating table rows on subsequent data chunks. **Review fix**: `SetValueAsync` moved outside try/catch with isolated error handling so timestamp persistence failure doesn't discard successfully fetched ADO data. | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-031 | TEST | Test cache-then-refresh: verify stale badge shown, data refreshed, badge removed after update. Mock `IContextStore.GetValueAsync("last_refreshed_at")` to return a stale timestamp, verify the stream yields `RefreshStarted`/`RefreshCompleted` chunks in correct order. **Review fix**: Added `DidNotReceive().SetValueAsync` assertion to `StaleCache_SeedRefreshFails_RestoresOriginalData`. **Review fix 2**: Added `StaleCache_TimestampPersistenceFails_StillDisplaysFreshData` test verifying that `SetValueAsync` failure after successful ADO re-fetch does not discard fresh data. | `tests/Twig.Cli.Tests/Rendering/CacheRefreshTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `twig workspace` shows cached data within 100ms
- [x] If cache is older than `cacheStaleMinutes`, a "cached (stale)" badge appears
- [x] Fresh data replaces stale data in-place once fetched
- [x] Badge is removed after successful refresh

---

### EPIC-007: Progressive Work Item Detail View

**Goal**: `twig show <id>` (future command or extended `twig status`) renders work item fields progressively.

**Prerequisites**: EPIC-001

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-032 | IMPL | Implement `SpectreRenderer.RenderWorkItemAsync`: render key fields (title, type, state) immediately, then progressively load extended fields (description, history). | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| ITEM-033 | IMPL | Create sub-plan document `docs/projects/twig-iux-show.plan.md`. | `docs/projects/twig-iux-show.plan.md` | DONE |

**Acceptance Criteria**:
- [x] Work item core fields render immediately
- [x] Extended fields populate progressively

---

### EPIC-008: Full TUI Mode (Tier 3)

**Goal**: Standalone `twig-tui` binary with Terminal.Gui-based Tree Navigator and Work Item Form Editor.

**Prerequisites**: EPIC-001 through EPIC-005 (Tier 1 + 2 complete), Terminal.Gui AOT evaluation

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-034 | IMPL | Create `src/Twig.Tui/Twig.Tui.csproj` project (non-AOT, references Terminal.Gui v2 beta). Add to `Twig.slnx`. | `src/Twig.Tui/Twig.Tui.csproj`, `Twig.slnx` | DONE |
| ITEM-035 | IMPL | Implement TUI entry point (`Program.cs`) with Terminal.Gui v2 instance-based API: `using var app = Application.Create().Init()`. Main window and menu bar. | `src/Twig.Tui/Program.cs` | DONE |
| ITEM-036 | IMPL | Implement `TreeNavigatorView`: Terminal.Gui `TreeView` with work item hierarchy, Vim keybindings (j/k/Enter/q), lazy child loading. | `src/Twig.Tui/Views/TreeNavigatorView.cs` | DONE |
| ITEM-037 | IMPL | Implement `WorkItemFormView`: Terminal.Gui form with editable fields, dirty indicators, save button. | `src/Twig.Tui/Views/WorkItemFormView.cs` | DONE |
| ITEM-038 | IMPL | Add `twig tui` subcommand in main CLI that launches the `twig-tui` binary using `Process.Start()` + `WaitForExit()` + `Environment.Exit(process.ExitCode)` for exit code propagation. Look for `twig-tui` in PATH and adjacent directory. **Note**: There is no `exec()` syscall on Windows — `Process.Start` + wait + exit code propagation is the correct Windows pattern. | `src/Twig/Program.cs` | DONE |
| ITEM-039 | IMPL | Create sub-plan document `docs/projects/twig-iux-tui.plan.md`. | `docs/projects/twig-iux-tui.plan.md` | DONE |
| ITEM-040 | TEST | Integration test: launch TUI, navigate tree, edit a field, verify changes persisted. | `tests/Twig.Tui.Tests/` | DONE |

**Acceptance Criteria**:
- [x] `twig-tui` launches a full-screen Terminal.Gui application
- [x] Tree Navigator displays work item hierarchy with Vim keybindings
- [x] Work Item Form Editor allows field editing with dirty indicators
- [x] `twig tui` from the main CLI launches the TUI binary

---

## Sub-Plan Stubs

The following sub-plan documents will be created as detailed plans for each scenario. They are tracked as work items in the epics above.

| Sub-Plan | Scope | Epic |
|----------|-------|------|
| `docs/projects/twig-iux-workspace.plan.md` | Progressive workspace rendering: table layout, column widths, spinner UX, badge placement, Spectre.Console Table/Panel mapping | EPIC-002 |
| `docs/projects/twig-iux-tree.plan.md` | Progressive tree rendering: Spectre.Console Tree widget, expand/collapse, child lazy-loading, box-drawing parity | EPIC-003 |
| `docs/projects/twig-iux-status.plan.md` | Status dashboard: Panel layout, Grid structure, pending change summary, hint integration | EPIC-004 |
| `docs/projects/twig-iux-show.plan.md` | Progressive work item detail: field-by-field loading, extended field support | EPIC-007 |
| `docs/projects/twig-iux-disambiguation.plan.md` | Interactive selection: SelectionPrompt configuration, filtering, keyboard UX, fallback behavior | EPIC-005 |
| `docs/projects/twig-iux-tui.plan.md` | Full TUI: Terminal.Gui architecture, view hierarchy, keybinding map, data binding, separate binary | EPIC-008 |

---

## References

- [Twig Requirements Document](./twig.req.md) — DI-004, RD-019, RD-021, FR-016, NFR-001
- [Spectre.Console Documentation](https://spectreconsole.net/) — Live Display, Table, Tree, SelectionPrompt, Status
- [Spectre.Console NuGet (v0.54.0)](https://www.nuget.org/packages/spectre.console) — Latest stable release
- [Spectre.Console Native AOT Template](https://github.com/Xairooo/Spectre.ConsoleNativeAOT.Template) — Community AOT validation
- [dotnet/runtime#115431](https://github.com/dotnet/runtime/issues/115431) — TypeConverterHelper IL2067 trim warning on .NET 9 (Aspire CLI repro)
- [Spectre.Console AOT Support Issue #1332](https://github.com/spectreconsole/spectre.console/issues/1332) — AOT feature request and discussion
- [Terminal.Gui v2 Beta Announcement](https://blog.kindel.com/2026/03/05/terminal-gui-still-absurd-now-beta/) — March 2026 beta release with instance-based API
- [Terminal.Gui AOT Issue #3109](https://github.com/gui-cs/Terminal.Gui/issues/3109) — AOT compatibility tracker
- [Terminal.Gui AOT Discussion #2414](https://github.com/gui-cs/Terminal.Gui/discussions/2414) — AOT support status
- [.NET Native AOT Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) — contoso docs
- [Spectre.Console Live Display Docs](https://spectreconsole.net/console/live/live-display) — Live context API, `UpdateTarget`, vertical overflow
- [Spectre.Console Status Docs](https://spectreconsole.net/console/live/status) — Status context limitations (not thread safe, mutually exclusive with Live)

## Change Log

- **2026-03-16**: EPIC-008 complete. Full TUI Mode (Tier 3): fixed dirty-state race condition in `LoadWorkItem` (`_isDirty = false` reset after originals set); added `when (ex is not OutOfMemoryException)` filter to pre-warm task exception handler so OOM causes clean crash instead of silent suppression; fixed `_savedEdits` cache to store only fields included in the `toSave` list preventing stale-edit masking on re-select; fixed `OnSave_NoDirtyFields_IsNoOp` test to assert `AddChangesBatchAsync` (the method actually called) instead of `AddChangeAsync`; updated sub-plan Design Decisions section to reflect the `when`-clause filter. All acceptance criteria satisfied.
- **2026-03-16**: EPIC-006 complete. Cache-Then-Refresh Pattern: all implementation (ITEM-028–030) was in place from prior iterations; ITEM-031 closed the final invariant gap by adding `StaleCache_TimestampPersistenceFails_StillDisplaysFreshData` test verifying that `SetValueAsync` failure after successful ADO re-fetch does not discard fresh data — exit code 0 and 'Refreshed Task' in output both verified. All acceptance criteria satisfied.
- **2026-03-16**: EPIC-005 complete. Interactive Disambiguation Prompts (Tier 2) review-fix pass: `OperationCanceledException` catch added to `SpectreRenderer.PromptDisambiguationAsync` ReadKey loop; `CancellationToken` threaded from `Program.cs` ConsoleAppFramework injection through `SetCommand.ExecuteAsync`, `NavigationCommands.DownAsync`, and `NavigationCommands.UpAsync` to `PromptDisambiguationAsync`; `cached.First()` replaced with `.FirstOrDefault()` null guard in `SetCommand.ExecuteAsync`; comment added to `UpAsync` explaining single-parent disambiguation asymmetry; explicit NSubstitute stubs added to `Down_MultiMatch_Tty_PromptsAndSelectsChild` and `Down_SingleMatch_NavigatesToChild` tests for deterministic parent-chain path.
