# twig show: Read-Only Work Item Lookup Without Context Change

> **Status**: ✅ Done

## Executive Summary

Implement a `twig show <id>` command that performs a **read-only, cache-only** work item
lookup by integer ID. Unlike `twig set`, this command does NOT change the active context,
does NOT trigger a sync/refresh, and does NOT record navigation history. It displays full
work item details (title, type, state, assignee, area, iteration, description, extended
fields, children, parent, links) using the existing rendering pipeline — SpectreRenderer
for interactive TTY, formatters for piped/JSON/minimal output. This provides a fast
"peek at any item" workflow that is safe to run mid-task without side effects.

## Background

### Current State

Twig's primary work item inspection commands are:

| Command | Reads cache | Sets context | Triggers sync | Records history |
|---------|------------|-------------|---------------|----------------|
| `twig set <id>` | ✅ (+ ADO fetch on miss) | ✅ | ✅ (target + parents) | ✅ |
| `twig status` | ✅ (active item) | — | ✅ (working set) | — |

There is currently **no way to inspect an arbitrary work item without side effects**.
Running `twig set <id>` to view item #200 while working on item #100 silently changes
context, triggers a sync, and records a navigation history entry — all undesirable when
the user just wants to peek.

### Architecture Context

The CLI uses a layered architecture:
- **Commands** (`src/Twig/Commands/`) — CLI entry points with primary constructors
- **Rendering** — dual-path: `IAsyncRenderer` (SpectreRenderer for TTY) or `IOutputFormatter` (sync formatters)
- **Data** — `IWorkItemRepository` for local SQLite cache, `IContextStore` for active context
- **DI** — commands registered in `CommandRegistrationModule.cs`, resolved lazily from `TwigCommands`

### Relevant Patterns

1. **SetCommand** (258 lines) — resolves by ID/pattern, sets context, syncs target + parents, renders via four-path pipeline
2. **StatusCommand** (327 lines) — reads active context, renders with extended fields, syncs working set after display
3. **Both commands** share the same rendering output: `FormatWorkItem()` on formatters, `RenderStatusAsync()` on SpectreRenderer

The `show` command is structurally a strict subset of `SetCommand`: same rendering, same
data lookups (children, parent, links, field definitions) — but without context mutation,
sync, ADO fetch, disambiguation, or navigation history.

## Problem Statement

Users frequently need to inspect work items other than their active context — checking
a parent epic's state, reviewing a sibling task's description, or verifying a linked item's
fields. Currently this requires `twig set <id>` which has three unwanted side effects:

1. **Context change** — the active work item shifts, disrupting the user's flow
2. **Sync trigger** — network calls to ADO add latency and may fail offline
3. **Navigation history pollution** — the inspection visit clutters the back/forward stack

Users must then `twig set <original-id>` to return, adding friction and another sync cycle.

## Goals and Non-Goals

### Goals

1. **G-1**: Provide a zero-side-effect work item lookup command (`twig show <id>`)
2. **G-2**: Read exclusively from local cache — zero network calls, instant response
3. **G-3**: Support all output formats (`--output human/json/json-full/json-compact/minimal`)
4. **G-4**: Follow the existing rendering pipeline (SpectreRenderer for TTY, sync path for piped/json)
5. **G-5**: Maintain AOT compatibility (no reflection, use source-gen patterns)
6. **G-6**: Provide clear error messages for missing/unknown IDs
7. **G-7**: Unit test coverage for happy path, missing ID, unknown ID, and output formats

### Non-Goals

- **NG-1**: Pattern-based lookup (title search) — `show` accepts integer IDs only for simplicity and predictability
- **NG-2**: ADO fetch on cache miss — if the item isn't cached, return an error with a hint to use `twig set <id>`
- **NG-3**: Pending change display — `show` displays the item as-is in cache; pending changes are context-specific and excluded by design
- **NG-4**: Background sync after display — this is a pure read operation
- **NG-5**: Interactive disambiguation — single integer ID means no ambiguity

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `twig show <id>` displays work item details from local cache |
| FR-2 | Active context (`IContextStore`) is NOT read or modified |
| FR-3 | No ADO fetch, sync, or refresh is triggered |
| FR-4 | Navigation history is NOT recorded |
| FR-5 | Output respects `--output` flag: human (default), json, json-full, json-compact, minimal. Note: `json` and `json-full` are equivalent — both resolve to `JsonOutputFormatter` via the `OutputFormatterFactory` switch (see `OutputFormatterFactory.GetFormatter()`). |
| FR-6 | TTY + human format uses `IAsyncRenderer.RenderStatusAsync()` for rich rendering |
| FR-7 | Non-TTY/piped/json/minimal uses `IOutputFormatter.FormatWorkItem()` |
| FR-8 | Unknown ID (not in cache) returns exit code 1 with error: "Work item #N not found in local cache. Run 'twig set N' to fetch it." |
| FR-9 | Child items, parent item, and links are loaded from cache (best-effort, no network) |
| FR-10 | Field definitions and status-fields config are loaded for extended field display |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible — no reflection, sealed class, primary constructor |
| NFR-2 | Zero network calls under all code paths |
| NFR-3 | Warnings-as-errors clean (`TreatWarningsAsErrors=true`) |
| NFR-4 | Telemetry event emitted with command name "show" (safe properties only) |

### Exit Code Definitions

| Code | Meaning | Example |
|------|---------|---------|
| 0 | Success | Work item found and displayed |
| 1 | General error / not found | Cache miss — item not in local cache |

Note: Exit code 2 (usage/argument error) is used by other commands (e.g., `SetCommand` returns 2 for empty `idOrPattern`) but does not apply to `show` because ConsoleAppFramework handles argument parsing and type conversion for the `int id` parameter before `ExecuteAsync` is called. Invalid input (non-integer) is caught at the framework level.

## Proposed Design

### Architecture Overview

```
User: twig show 42 --output json
  │
  ▼
Program.cs ── TwigCommands.Show([Argument] int id, string output, CancellationToken ct)
  │ services.GetRequiredService<ShowCommand>()
  ▼
ShowCommand.ExecuteAsync(int id, string outputFormat, CancellationToken ct)
  │
  ├─ Stopwatch.GetTimestamp() → startTimestamp
  │
  ├─ RenderingPipelineFactory.Resolve(outputFormat) → (fmt, renderer?)
  │
  ├─ IWorkItemRepository.GetByIdAsync(id) → WorkItem?
  │  └─ null? → stderr error, return 1
  │
  ├─ Enrichment (all cache-only, best-effort):
  │  ├─ IWorkItemRepository.GetChildrenAsync(id) → children
  │  ├─ IWorkItemRepository.GetByIdAsync(parentId) → parent  [if ParentId.HasValue]
  │  ├─ IWorkItemLinkRepository.GetLinksAsync(id) → links
  │  ├─ IFieldDefinitionStore.GetAllAsync() → fieldDefs
  │  ├─ StatusFieldsConfig.Parse(file) → statusFieldEntries  [if file exists]
  │  └─ ProcessConfigExtensions.ComputeChildProgress(children) → childProgress
  │
  ├─ Rendering (4 paths, mirroring SetCommand lines 178-224):
  │  ├─ renderer != null? → renderer.RenderStatusAsync(
  │  │     getItem: () => Task.FromResult<WorkItem?>(item),
  │  │     getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
  │  │     ct, fieldDefs, statusFieldEntries, childProgress, links, parent, children)
  │  ├─ fmt is HumanOutputFormatter? → humanFmt.FormatWorkItem(
  │  │     item, showDirty: false, fieldDefs, statusFieldEntries,
  │  │     childProgress, pendingChanges: null, links, parent, children)
  │  ├─ fmt is JsonOutputFormatter? → jsonFmt.FormatWorkItem(item, showDirty: false, links, parent, children)
  │  └─ else → fmt.FormatWorkItem(item, showDirty: false)
  │
  └─ telemetryClient?.TrackEvent("CommandExecuted", {command: "show", exit_code, output_format, ...})
```

### Key Components

#### 1. `ShowCommand` (new file: `src/Twig/Commands/ShowCommand.cs`)

A sealed class with a primary constructor accepting:
- `IWorkItemRepository` — cache-only item lookup
- `IWorkItemLinkRepository` — cache-only link lookup (no network, unlike `SyncCoordinator.SyncLinksAsync`)
- `OutputFormatterFactory` — sync formatter resolution
- `RenderingPipelineFactory?` — optional, for TTY rendering
- `TwigPaths?` — for status-fields config path
- `IFieldDefinitionStore?` — for extended field display
- `IProcessConfigurationProvider?` — for child progress computation (via extension method `ComputeChildProgress` in `ProcessConfigExtensions.cs`, not an interface method — see DD-6)
- `ITelemetryClient?` — for telemetry

**Deliberately excluded dependencies** (enforcing no-side-effect contract):
- ~~`IContextStore`~~ — no context read or write
- ~~`ActiveItemResolver`~~ — no ADO fetch fallback
- ~~`SyncCoordinator`~~ — no sync
- ~~`WorkingSetService`~~ — no working set computation
- ~~`INavigationHistoryStore`~~ — no history recording
- ~~`IAdoWorkItemService`~~ — no ADO calls
- ~~`IPromptStateWriter`~~ — no prompt state update
- ~~`IPendingChangeStore`~~ — no pending change retrieval (show displays cache-as-is)

This exclusion-by-construction approach makes it structurally impossible for `show` to
have side effects — the dependencies simply aren't available.

**Pending changes handling**: Because `IPendingChangeStore` is excluded:
- `IAsyncRenderer.RenderStatusAsync()` receives an empty-list factory for its required `getPendingChanges` parameter: `() => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([])`. This is the same pattern used by `SetCommand` when `pendingChangeStore` is null (see `SetCommand.cs` lines 180-182).
- `HumanOutputFormatter.FormatWorkItem()` receives `null` for its nullable `(int FieldCount, int NoteCount)?` `pendingChanges` parameter, which causes the formatter to omit the pending changes footer.

#### 2. `TwigCommands.Show()` Method Signature

The router method in `Program.cs` follows the existing `TwigCommands` pattern. Unlike `Set`
which accepts `string idOrPattern`, `Show` accepts `int id` directly — ConsoleAppFramework
handles parsing and type validation at the framework level:

```csharp
/// <summary>Display a work item from cache without changing context.</summary>
public async Task<int> Show([Argument] int id,
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default)
    => await services.GetRequiredService<ShowCommand>().ExecuteAsync(id, output, ct);
```

This mirrors the `Set` method pattern (`Program.cs` line 298) but with `int id` instead of
`string idOrPattern`, and delegates to `ShowCommand.ExecuteAsync(int, string, CancellationToken)`.

#### 3. Link Loading Strategy

`SetCommand` and `StatusCommand` use `SyncCoordinator.SyncLinksAsync()` which fetches
links from ADO (network call). Since `show` must be cache-only, it uses
`IWorkItemLinkRepository.GetLinksAsync()` directly — reading whatever links are already
cached from prior syncs. This means links may be stale or absent for items that haven't
been recently synced, which is an acceptable trade-off for zero-network guarantee
(see Risk R-1).

### Data Flow

1. **Input**: Integer ID from CLI `[Argument] int id` parameter
2. **Pipeline resolution**: `RenderingPipelineFactory.Resolve(outputFormat)` → `(IOutputFormatter fmt, IAsyncRenderer? renderer)`
3. **Cache lookup**: `IWorkItemRepository.GetByIdAsync(id)` — returns `WorkItem?`
4. **Error path**: If null, write error to stderr: `"Work item #{id} not found in local cache. Run 'twig set {id}' to fetch it."`, return exit code 1
5. **Enrichment** (all cache-only, best-effort):
   - Children via `IWorkItemRepository.GetChildrenAsync(id)`
   - Parent via `IWorkItemRepository.GetByIdAsync(parentId)` if `item.ParentId.HasValue`
   - Links via `IWorkItemLinkRepository.GetLinksAsync(id)` — cache-only, no network (DD-2)
   - Field definitions via `IFieldDefinitionStore.GetAllAsync()`
   - Status fields via `StatusFieldsConfig.Parse(content)` from `TwigPaths.StatusFieldsPath` file (if file exists)
   - Child progress via `processConfigProvider.ComputeChildProgress(children)` — this is an **extension method** defined in `ProcessConfigExtensions.cs` (`Twig.Domain.Extensions` namespace), not a method on `IProcessConfigurationProvider` itself. It calls `provider.SafeGetConfiguration()` per child and resolves state categories to compute `(int Done, int Total)?`.
6. **Rendering** (four paths, mirroring `SetCommand` lines 178-224):
   - **Path A (TTY + human)**: `renderer.RenderStatusAsync(getItem, getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]), ct, fieldDefs, statusFieldEntries, childProgress, links, parent, children)`
   - **Path B (non-TTY human)**: `humanFmt.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries, childProgress, pendingChanges: null, links, parent, children)`
   - **Path C (JSON)**: `jsonFmt.FormatWorkItem(item, showDirty: false, links, parent, children)`
   - **Path D (other)**: `fmt.FormatWorkItem(item, showDirty: false)`
7. **Telemetry**: `telemetryClient?.TrackEvent("CommandExecuted", ...)` with `command: "show"`, exit code, output format, version, OS platform, duration_ms (NFR-4)

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Integer-only ID (no pattern matching) | Eliminates disambiguation complexity; pattern search is a `set` concern |
| DD-2 | Cache-only links via `IWorkItemLinkRepository` | Avoids `SyncCoordinator.SyncLinksAsync()` which makes network calls |
| DD-3 | No `IContextStore` dependency | Structurally prevents context mutation — can't accidentally read or write active ID |
| DD-4 | Exit code 1 for cache miss | Consistent with `set` command's "not found" exit code. Exit code 2 is reserved for usage/argument errors (e.g., `SetCommand` returns 2 for empty `idOrPattern` at line 67) but does not apply here because ConsoleAppFramework validates the `int id` parameter at the framework level. |
| DD-5 | Hint to use `twig set <id>` on cache miss | Guides user to the fetch-capable command |
| DD-6 | `ComputeChildProgress` is an extension method | Called as `processConfigProvider.ComputeChildProgress(children)` but defined in `ProcessConfigExtensions.cs` (`Twig.Domain.Extensions`), not on `IProcessConfigurationProvider`. Implementers should look in `ProcessConfigExtensions.cs`, not the interface definition. |
| DD-7 | `IPendingChangeStore` excluded; empty factory for renderer | `RenderStatusAsync` requires a `Func<Task<IReadOnlyList<PendingChangeRecord>>>` — cannot be null. ShowCommand passes `() => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([])` to satisfy the contract while ensuring no pending changes are displayed. `HumanOutputFormatter.FormatWorkItem` accepts `null` for its `(int FieldCount, int NoteCount)?` parameter. |

## Alternatives Considered

| Alternative | Pros | Cons | Verdict |
|------------|------|------|---------|
| **`--readonly` / `--peek` flag on `twig set`** | No new command; reuses existing rendering code inline | Increases `SetCommand` complexity (already 258 lines); requires guarding every side-effect call behind a flag; easy to miss a guard when adding future side effects; conflates two distinct intents (navigate-to vs. peek-at) in a single command | **Rejected** — a separate command enforces the no-side-effect contract structurally via excluded DI dependencies, whereas a flag relies on runtime conditionals that can regress. |
| **Live ADO fetch with `--fetch` opt-in flag** | Supports inspecting items not yet cached | Violates the zero-network principle; adds complexity for a rare edge case; `twig set <id>` already serves this purpose | **Deferred** — may be added later as an opt-in flag if demand warrants |

The "new command" approach was chosen because **exclusion-by-construction** (not injecting `IContextStore`, `SyncCoordinator`, etc.) is a stronger guarantee than runtime branching. `ShowCommand` structurally cannot have side effects because the dependencies simply aren't available — no `if (readonly)` guard can be missed in future maintenance.

## Dependencies

### External
- None new. Uses existing Spectre.Console, ConsoleAppFramework, SQLite.

### Internal
- `IWorkItemLinkRepository` — already registered in `TwigServiceRegistration.cs`
- `IFieldDefinitionStore` — already registered
- `IProcessConfigurationProvider` — already registered
- `ProcessConfigExtensions.ComputeChildProgress()` — already available via `Twig.Domain.Extensions`
- All rendering infrastructure — already registered

### Sequencing
- No prerequisites. All required interfaces and infrastructure already exist.

## Risks and Mitigations

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R-1 | **Stale or missing link data**: `IWorkItemLinkRepository.GetLinksAsync()` reads cached links only. Items that haven't been recently synced may show no links or outdated links, which could confuse users expecting current data. | Medium | Low | This is an inherent trade-off of the zero-network guarantee (G-2). The error message for cache misses already guides users to `twig set <id>` for a full fetch. A future enhancement could add a `--fetch` flag to opt into network calls, but this is out of scope (NG-2). |


## Open Questions

None. All requirements are clear and non-blocking.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/ShowCommand.cs` | Core command implementation (~130 lines) |
| `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | Unit tests (~250 lines) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Add `services.AddSingleton<ShowCommand>();` to `AddCoreCommands()` (after `SetCommand` registration at line 44) |
| `src/Twig/Program.cs` | Add `Show()` method to `TwigCommands` class (after `Set` method, ~line 299) |

## ADO Work Item Structure

### Issue #1351: twig show — read-only work item lookup without context change or sync

**Goal**: Deliver a complete, tested `twig show <id>` command that reads from cache only,
displays full work item details, and has zero side effects.

**Prerequisites**: None

#### Tasks

| Task ID | Description | Files | Effort | Requirements |
|---------|-------------|-------|--------|-------------|
| T1 | Implement `ShowCommand` class (see T1 Implementation Notes below) | `src/Twig/Commands/ShowCommand.cs` | ~130 LoC | FR-1 through FR-10, NFR-1, NFR-2, NFR-4, DD-1 through DD-7 |
| T2 | Register `ShowCommand` in DI (`services.AddSingleton<ShowCommand>();` in `CommandRegistrationModule.AddCoreCommands()`) and add `Show()` router method to `TwigCommands` class in `Program.cs` with signature `public async Task<int> Show([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `src/Twig/Program.cs` | ~5 LoC | FR-1, FR-5, NFR-1, G-3 |
| T3 | Write unit tests: (1) happy path — cached item returns exit 0, correct output, (2) cache miss — returns exit 1 with stderr error message, (3) output format variants — human, json, json-compact, minimal each produce expected output, (4) TTY rendering path — `RenderStatusAsync` called with empty `getPendingChanges` factory, (5) parent/children/links enrichment — all cache lookups performed, (6) **telemetry emission** — verify `ITelemetryClient.TrackEvent` called with `command: "show"`, correct exit code, output format, and duration_ms metric | `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | ~250 LoC | FR-1 through FR-10, NFR-2, NFR-4, G-7 |

**T1 Implementation Notes:**
- **Class structure**: Sealed class with primary constructor, injecting only cache/rendering dependencies (NFR-1)
- **Method signature**: `ExecuteAsync(int id, string outputFormat, CancellationToken ct)` returning `Task<int>`
- **Cache lookup**: `IWorkItemRepository.GetByIdAsync(id)` — return exit 1 with stderr error on null (FR-1, FR-3, FR-8, DD-4)
- **Enrichment** (all cache-only, best-effort): children, parent, links, field definitions, status fields, child progress (FR-9, FR-10)
- **Rendering pipeline**: Four-path pipeline mirroring `SetCommand` lines 178-224 (FR-6, FR-7):
  - Path A: `renderer.RenderStatusAsync()` with empty `getPendingChanges` factory (DD-7)
  - Path B: `humanFmt.FormatWorkItem()` with `null` pendingChanges
  - Path C: `jsonFmt.FormatWorkItem()` with links/parent/children
  - Path D: generic `fmt.FormatWorkItem()` fallback
- **Telemetry**: Emit `CommandExecuted` event with safe properties only (NFR-4)
- **Excluded dependencies**: No `IContextStore`, `SyncCoordinator`, `INavigationHistoryStore`, `IAdoWorkItemService`, `IPendingChangeStore` (FR-2, FR-3, FR-4, DD-3)

**Acceptance Criteria**:
- [ ] `twig show <id>` displays work item details when item is in cache (FR-1)
- [ ] `twig show <id>` returns exit 1 with helpful error when item is not in cache (FR-8)
- [ ] No dependency on `IContextStore`, `IAdoWorkItemService`, `SyncCoordinator`, or `INavigationHistoryStore` (FR-2, FR-3, FR-4)
- [ ] Output respects `--output human/json/json-full/json-compact/minimal` flags (FR-5, G-3)
- [ ] SpectreRenderer used for TTY + human format with empty `getPendingChanges` factory; formatters for all other paths (FR-6, FR-7, DD-7)
- [ ] All existing tests continue to pass (NFR-3)
- [ ] New unit tests cover: happy path, cache miss, each output format, TTY path, enrichment data, telemetry emission (G-7, NFR-4)

## PR Groups

### PR Group 1: `twig show` command (all tasks)

| Attribute | Value |
|-----------|-------|
| **Tasks** | T1, T2, T3 |
| **Classification** | Deep — few files, focused implementation |
| **Estimated LoC** | ~385 (130 command + 5 wiring + 250 tests) |
| **Files** | 4 (2 new, 2 modified) |
| **Successors** | None |
| **Reviewability** | Well under 2000 LoC / 50 file limits. Single coherent feature. |

This is a single-PR feature — all tasks are tightly coupled and the total scope is
small enough for a single reviewable unit.

## References

- `src/Twig/Commands/SetCommand.cs` — primary reference for rendering pattern (lines 151-224), pending changes handling (lines 180-182), telemetry (lines 44-54)
- `src/Twig/Commands/StatusCommand.cs` — reference for extended field loading and dual-path rendering
- `src/Twig/Rendering/RenderingPipelineFactory.cs` — `Resolve(string outputFormat, bool noLive)` method for TTY detection and pipeline selection
- `src/Twig/Rendering/IAsyncRenderer.cs` — `RenderStatusAsync` signature with required `getPendingChanges` parameter (line 34-43)
- `src/Twig/Formatters/OutputFormatterFactory.cs` — `GetFormatter()` switch for format resolution
- `src/Twig/Formatters/HumanOutputFormatter.cs` — `FormatWorkItem` overload with nullable `(int FieldCount, int NoteCount)?` pendingChanges parameter (lines 64-72)
- `src/Twig.Domain/Extensions/ProcessConfigExtensions.cs` — `ComputeChildProgress` extension method (lines 35-50)
- `src/Twig/DependencyInjection/CommandRegistrationModule.cs` — `AddCoreCommands()` registration pattern
- `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` — test pattern reference (NSubstitute mocking, Shouldly assertions)
