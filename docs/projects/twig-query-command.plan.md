# twig query: Ad-hoc Work Item Search and Filtering

> **Status**: 🔨 In Progress — 1/2 PR groups merged  
> **Epic**: #1302  

## Executive Summary

Today, finding an arbitrary work item forces users out of the terminal and into the
ADO web UI — a costly context switch during triage and planning conversations.
`twig query` eliminates that friction with a CLI-native search command. Users can
search by keyword (`twig query 'MCP server'`), filter by type/state/assignee
(`twig query --state Doing --type Issue`), and apply time-based filters
(`twig query --changed-since 7d`). The command builds WIQL queries from CLI flags,
executes them via `IAdoWorkItemService.QueryByWiqlAsync()` with server-side `$top`
limiting, renders results as a Spectre.Console table (or JSON/IDs output), and caches
discovered items in the local SQLite store for subsequent `twig show` lookups.

## Background

### Current State

Twig's work item discovery is currently limited to two scopes:

| Command | Scope | Data Source | Filters |
|---------|-------|-------------|---------|
| `twig workspace` | Current sprint, current user | Local cache (from last refresh) | None — shows all sprint items |
| `twig tree` | Hierarchy under active item | Local cache | None — shows all children |
| `twig set <pattern>` | Title pattern match | Local cache + ADO fetch | Title substring only |

There is **no way to search across the full project** by type, state, assignee, area
path, or time range without leaving the terminal and using the ADO web UI or writing
raw WIQL.

### Architecture Context

The existing WIQL infrastructure is mature and battle-tested:

- **`IAdoWorkItemService.QueryByWiqlAsync(string wiql, CancellationToken ct)`** — executes
  a WIQL query via the ADO REST API (`_apis/wit/wiql?api-version=7.1`) and returns a list
  of matching work item IDs.
- **`IAdoWorkItemService.FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct)`** —
  fetches full work item details in batches of ≤200 with relations expanded.
- **`IWorkItemRepository.SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct)`**
  — persists fetched items to the local SQLite cache for offline access.

WIQL query building is currently done inline in two commands:

| Location | Pattern |
|----------|---------|
| `RefreshCommand` (L71–108) | Builds `SELECT [System.Id] FROM WorkItems WHERE [System.IterationPath] = '...'` with area path UNDER clauses, single-quote escaping |
| `InitCommand` (L334–361) | Same WIQL pattern with area path and type filters |

Both commands use string interpolation with `Replace("'", "''")` for WIQL injection
prevention. The query command will extract this pattern into a reusable, testable
`WiqlQueryBuilder` service in the domain layer.

### Call-Site Audit: IAdoWorkItemService.QueryByWiqlAsync

The query command introduces a **new overload** of `QueryByWiqlAsync` with a `top`
parameter (see DD-01). The existing `QueryByWiqlAsync(string, CancellationToken)`
overload remains unchanged. All existing callers are unaffected:

| File | Method | Current Call Pattern | Impact |
|------|--------|---------------------|--------|
| `AdoRestClient.cs` L83 | `FetchChildrenAsync` | `QueryByWiqlAsync(wiql, ct)` — uses existing overload | None |
| `AdoRestClient.cs` L126 | `QueryByWiqlAsync` (implementation, not a call site) | Defines the existing overload | Adds new `$top` overload alongside |
| `RefreshCommand.cs` L108 | `ExecuteCoreAsync` | `QueryByWiqlAsync(wiql)` — uses existing overload | None |
| `InitCommand.cs` L361 | Init flow | `QueryByWiqlAsync(wiql)` — uses existing overload | None |
| `RefreshOrchestrator.cs` L56 | `FetchItemsAsync` | `QueryByWiqlAsync(wiql, ct)` — uses existing overload | None |

### Call-Site Audit: IOutputFormatter

`FormatQueryResults()` is added to `IOutputFormatter` as a new interface method.
All 4 implementing classes are updated atomically in PR Group 2:

| File | Class | Action |
|------|-------|--------|
| `HumanOutputFormatter.cs` | `HumanOutputFormatter` | New table rendering method |
| `JsonOutputFormatter.cs` | `JsonOutputFormatter` | New JSON object with count + items |
| `JsonCompactOutputFormatter.cs` | `JsonCompactOutputFormatter` | Delegate to full JSON |
| `MinimalOutputFormatter.cs` | `MinimalOutputFormatter` | Explicit `return string.Empty;` stub (NG-7) |

## Problem Statement

During daily triage and planning, users frequently need to find arbitrary work items
that fall outside the current sprint/workspace view:

1. **Keyword search** — "Which items mention 'MCP server'?" requires opening the ADO
   web UI and using the search box.
2. **State filtering** — "Show me all open Issues" requires navigating ADO queries or
   backlogs manually.
3. **Assignee filtering** — "What's assigned to me across all iterations?" has no CLI answer.
4. **Recency filtering** — "What changed in the last week?" requires ADO dashboards.

Each of these interrupts the terminal-native workflow that twig is designed to preserve.
The cognitive cost of context-switching to a browser, finding the item, and returning
to the terminal is high — especially for quick lookups during planning conversations.

## Goals and Non-Goals

### Goals

1. **G-1**: Free-text search across work item titles via `twig query 'keyword'`
2. **G-2**: Filter by `--type`, `--state`, `--assigned-to`, `--area-path`, `--iteration-path`
3. **G-3**: Time-based filters via `--created-since` and `--changed-since` with shorthand
   notation (`7d`, `2w`, `1m`)
4. **G-4**: Multiple output formats: table (default), `--output json`, `--output ids`
5. **G-5**: Configurable result limit via `--top N` (default 25)
6. **G-6**: Cache discovered items locally so `twig show <id>` works on query results
7. **G-7**: Reusable, testable WIQL query builder extracted to the domain layer
8. **G-8**: Process-agnostic — no hardcoded type names, state names, or field references
   beyond System.* fields
9. **G-9**: AOT-compatible with no reflection (source-gen JSON, no dynamic types)
10. **G-10**: Respect area path defaults from workspace config when no `--area-path` is
    specified

### Non-Goals

- **NG-1**: Saved named queries (`twig query --save 'my-bugs'`) — stretch goal, deferred
- **NG-2**: Raw WIQL passthrough (`twig query --wiql 'SELECT ...'`) — stretch goal, deferred
- **NG-3**: Description search — WIQL `CONTAINS` on `System.Description` is expensive
  and rarely useful; title search covers 90%+ of use cases
- **NG-4**: Interactive result selection / disambiguation — users pipe IDs or use
  `twig set` explicitly
- **NG-5**: Async progressive rendering — query results are fetched all-at-once (not
  streamed), so sync rendering is sufficient for v1
- **NG-6**: Full-text search indexing — ADO's WIQL `CONTAINS` operator handles server-side
  text matching
- **NG-7**: `--output minimal` format for query results — not in the acceptance criteria;
  human/json/ids covers all required output formats

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig query 'keyword'` returns items where title contains the keyword |
| FR-02 | `--type <name>` filters by work item type (process-agnostic, user supplies the type name) |
| FR-03 | `--state <name>` filters by work item state |
| FR-04 | `--assigned-to <name>` filters by assignee display name |
| FR-05 | `--area-path <path>` filters by area path (uses UNDER operator) |
| FR-06 | `--iteration-path <path>` filters by iteration path (uses UNDER operator) |
| FR-07 | `--created-since <duration>` filters items created within the specified timeframe |
| FR-08 | `--changed-since <duration>` filters items changed within the specified timeframe |
| FR-09 | Duration shorthand supports `Nd` (days), `Nw` (weeks), `Nm` (months) |
| FR-10 | `--top N` limits results server-side via the ADO `$top` API parameter (default 25) |
| FR-11 | Default output: human-readable table with ID, Type, Title, State, AssignedTo columns |
| FR-12 | `--output json` returns structured JSON array with metadata |
| FR-13 | `--output ids` returns one ID per line for piping to `twig set` |
| FR-14 | All filters are combinable (AND logic) |
| FR-15 | Results are ordered by `[System.ChangedDate] DESC` by default |
| FR-16 | Fetched items are cached in local SQLite for `twig show` |
| FR-17 | When no `--area-path` is specified, respect config `defaults.areaPath` / `defaults.areaPaths` / `defaults.areaPathEntries` |
| FR-18 | WIQL injection is prevented by escaping single quotes in all string parameters |
| FR-19 | Contextual hints are suppressed for `--output ids` and `--output json` formats. Note: minimal format is out of scope per NG-7 and never reaches the hint path, so no suppression logic is needed for it. |
| FR-20 | Invalid `--created-since` or `--changed-since` values produce a clear error message (`error: Invalid duration '...'. Use format: Nd, Nw, Nm.`) and exit code 1 |
| FR-21 | Running `twig query` with no search text and no filters is permitted — queries all items scoped by default area paths (if configured) and limited by `--top` (see DD-10) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | AOT-compatible: no reflection, source-gen JSON context for any new types |
| NFR-02 | Process-agnostic: no hardcoded type/state names in domain logic |
| NFR-03 | Exit code 0 for success (even with zero results), 1 for errors |
| NFR-04 | Network errors produce actionable error messages via the existing exception pipeline (see §Exception Pipeline below) |
| NFR-05 | Zero results produce a clear "No items found" message with hints |
| NFR-06 | Telemetry tracks command name, exit code, duration, result count (no PII) |

#### Exception Pipeline (NFR-04)

Network errors flow through the existing two-layer exception pipeline without any
changes to `twig query`:

1. **Infrastructure layer** — `AdoErrorHandler` (in `Twig.Infrastructure.Ado`) inspects
   HTTP responses and throws typed exceptions (`AdoOfflineException`,
   `AdoAuthenticationException`, `AdoNotFoundException`, etc.).
2. **CLI layer** — `ExceptionFilter` (L169 in `Program.cs`) catches unhandled exceptions
   and delegates to `ExceptionHandler.Handle()` (L198), which maps each exception type
   to a user-facing stderr message and exit code.

`QueryCommand` does not need custom exception handling — the global pipeline covers
all ADO error cases.

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  CLI Layer (Twig)                                           │
│  ┌──────────────┐   ┌───────────────────┐   ┌───────────┐  │
│  │ QueryCommand │──▶│ OutputFormatters   │──▶│ Console   │  │
│  │              │   │ (Human/JSON/IDs)   │   │ stdout    │  │
│  └──┬───────────┘   └───────────────────┘   └───────────┘  │
│     │ (uses directly)                                       │
├─────┼───────────────────────────────────────────────────────┤
│  Domain Layer (Twig.Domain)                                 │
│     ├──▶ ┌───────────────────┐  ┌───────────────────┐       │
│     │    │ WiqlQueryBuilder  │  │ QueryParameters   │       │
│     │    │ (pure, static)    │  │ (value object)    │       │
│     │    └───────────────────┘  └───────────────────┘       │
│     └──▶ ┌───────────────────┐                              │
│          │ QueryResult       │                              │
│          │ (read model)      │                              │
│          └───────────────────┘                              │
├─────┼───────────────────────────────────────────────────────┤
│  Infrastructure Layer (Twig.Infrastructure)                 │
│  ┌──┴───────────┐   ┌───────────────────┐                   │
│  │ AdoRestClient│──▶│ SQLite Cache      │                   │
│  │ (+$top overl)│   │ (existing)        │                   │
│  └──────────────┘   └───────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `QueryParameters` (Value Object)

```csharp
// src/Twig.Domain/ValueObjects/QueryParameters.cs
public sealed record QueryParameters
{
    public string? SearchText { get; init; }
    public string? TypeFilter { get; init; }
    public string? StateFilter { get; init; }
    public string? AssignedToFilter { get; init; }
    public string? AreaPathFilter { get; init; }
    public string? IterationPathFilter { get; init; }
    public int? CreatedSinceDays { get; init; }
    public int? ChangedSinceDays { get; init; }
    public int Top { get; init; } = 25;
    public IReadOnlyList<(string Path, bool IncludeChildren)>? DefaultAreaPaths { get; init; }
}
```

Sealed record with init-only properties. Immutable, testable, no dependencies.
`DefaultAreaPaths` carries both path and `IncludeChildren` flag as value tuples,
preserving the per-entry scoping semantics from `AreaPathEntry` in config. The
`WiqlQueryBuilder` emits `UNDER` when `IncludeChildren` is true or `=` for exact
match — **matching the established `RefreshCommand` behavior** (L77–87). The
`QueryCommand` maps config `AreaPathEntry` values into these tuples at the
command boundary.

#### 2. `WiqlQueryBuilder` (Domain Service)

```csharp
// src/Twig.Domain/Services/WiqlQueryBuilder.cs
public static class WiqlQueryBuilder
{
    public static string Build(QueryParameters parameters) { ... }
    internal static string EscapeWiql(string value) => value.Replace("'", "''");
}
```

Pure function: takes `QueryParameters`, returns a WIQL string. Handles:
- `CONTAINS` for title search text
- `=` for type, state, assigned-to exact matches
- `UNDER` for explicit `--area-path` / `--iteration-path` CLI flags
- `UNDER` or `=` for default area paths based on each entry's `IncludeChildren` flag
  (matching `RefreshCommand` L84: `entry.IncludeChildren ? "UNDER" : "="`)
- `>=` with `@Today - N` for time filters
- Default area path from config when no explicit area path filter
- `ORDER BY [System.ChangedDate] DESC`
- Single-quote escaping for injection prevention

> **Note**: The `Top` property on `QueryParameters` is **not** embedded in the WIQL
> string. WIQL syntax does not support a `TOP N` clause. Instead, `Top` is passed
> as the `$top` query parameter on the ADO REST API call (see DD-01).

#### 3. `QueryResult` (Read Model)

```csharp
// src/Twig.Domain/ReadModels/QueryResult.cs
public sealed record QueryResult(
    IReadOnlyList<WorkItem> Items,
    bool IsTruncated);
```

Carries query results plus a truncation flag. `TotalMatchCount` is omitted: the ADO
`$top` API returns at most N IDs with no total-count field, so `TotalMatchCount`
would always equal `Items.Count`. The JSON formatter uses `items.Count` directly.

**`IsTruncated` heuristic**: See DD-09 in Design Decisions for the full trade-off
analysis. `IsTruncated` is computed as `items.Count >= top`. The `QueryCommand`
constructs `QueryResult` using this heuristic after the `FetchBatchAsync` call:
`new QueryResult(items, items.Count >= top)`.

> **Design trade-off**: `QueryResult` is labeled a "read model" but wraps
> `IReadOnlyList<WorkItem>`, where `WorkItem` is a mutable aggregate with an
> internal command queue. This is a *pragmatic* choice — not a pure read model in
> the CQRS sense. `QueryResult` itself is immutable (sealed record), and the
> `WorkItem` instances it carries are used read-only by the formatting pipeline.
> Creating a separate query-specific DTO would duplicate fields already on
> `WorkItem` and break the existing `SaveBatchAsync` cache path which expects
> `WorkItem` instances. The read-model label describes the *role* (projection of
> query results for display), not the purity of the underlying type.

#### 4. `QueryCommand` (CLI Command)

```csharp
// src/Twig/Commands/QueryCommand.cs
public sealed class QueryCommand(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
```

> **`TextWriter? stderr` rationale**: The injected `TextWriter? stderr` parameter is
> specifically for the `ParseDuration` validation error path (FR-20). When a user
> provides an invalid `--created-since` or `--changed-since` value, the command
> writes a structured error message to stderr and returns exit code 1 — before any
> WIQL query is built. Existing commands don't use this pattern because they lack
> pre-query validation; `QueryCommand` is unique in validating user input before
> the ADO call. The parameter defaults to `Console.Error` and is injectable for
> test assertions.

> **Config coupling note**: `QueryCommand` reads `config.Defaults.AreaPathEntries` to
> apply default area path scoping (DD-07). This follows the same pattern as
> `RefreshCommand` (L77–105), which already reads `config.Defaults?.AreaPathEntries`
> from the command layer. See Risk R-5 for the coupling concern and mitigation.

Execution flow:
1. Parse `--created-since` / `--changed-since` via private `ParseDuration` static helper (inline in `QueryCommand`; returns `int?`, null on invalid input)
2. Build `QueryParameters` from CLI args + config defaults (mapping
   `AreaPathEntry` values into `(Path, IncludeChildren)` tuples at the command boundary)
3. Generate WIQL via `WiqlQueryBuilder.Build()`
4. Execute `adoService.QueryByWiqlAsync(wiql, top, ct)` → get IDs (server-side limited)
5. Execute `adoService.FetchBatchAsync(ids, ct)` → get full items
6. Cache via `workItemRepo.SaveBatchAsync(items, ct)`
7. **Branch on output format**:
   - If `output == "ids"`: print one ID per line via `string.Join("\n", ids)` — skip formatter **and** hint emission entirely (DD-04, FR-19)
   - Otherwise: resolve formatter via `OutputFormatterFactory` and call `FormatQueryResults()`
8. Emit hints (if not already skipped in step 7) — HintEngine's existing suppression handles `json` and `minimal` formats (L53–55); for `ids` format, the command itself bypasses hint emission in the step 7 branch above
9. Track telemetry (command, duration, result_count, exit_code)

#### 5. Output Formatting

**Human format** — Spectre-style ANSI table:
```
  Query: title contains 'MCP server', state = 'Doing'
  Found 3 items

  #1234  🔵 Issue   MCP server integration         Doing   Daniel Green
  #1235  🟢 Task    MCP server auth module          Done    Daniel Green
  #1240  ⚪ Task    MCP server error handling       To Do   Daniel Green

  hint: Use 'twig set <id>' to navigate to an item.
```

**JSON format** — structured output:
```json
{
  "query": "title contains 'MCP server' AND state = 'Doing'",
  "count": 3,
  "truncated": false,
  "items": [
    {"id": 1234, "type": "Issue", "title": "MCP server integration", "state": "Doing", "assignedTo": "Daniel Green"}
  ]
}
```

**IDs format** — one per line for piping:
```
1234
1235
1240
```

> **Note (NG-7)**: Minimal format (`--output minimal`) is explicitly out of scope for
> query results. The `MinimalOutputFormatter.FormatQueryResults()` implementation
> returns `string.Empty`. If minimal query output is needed in the future, it can be
> added without changing the interface.

### Data Flow

```
User invokes: twig query 'MCP server' --state Doing --top 10

  1. CLI parses args → QueryCommand.ExecuteAsync()
  2. Build QueryParameters { SearchText: "MCP server", StateFilter: "Doing", Top: 10 }
  3. WiqlQueryBuilder.Build(params) →
     "SELECT [System.Id] FROM WorkItems
      WHERE [System.Title] CONTAINS 'MCP server'
        AND [System.State] = 'Doing'
      ORDER BY [System.ChangedDate] DESC"
  4. AdoRestClient.QueryByWiqlAsync(wiql, top: 10, ct) →
     POST _apis/wit/wiql?$top=10&api-version=7.1
     → [1234, 1235, 1240]   (server returns at most 10 IDs)
  5. AdoRestClient.FetchBatchAsync([1234, 1235, 1240]) → [WorkItem, WorkItem, WorkItem]
  6. SqliteWorkItemRepository.SaveBatchAsync(items, ct) → cached locally
  7. Construct QueryResult: new QueryResult(items, IsTruncated: items.Count >= 10)
  8. Branch on output format:
     ├─ output == "ids" → Console.Write("1234\n1235\n1240")
     │                     [skip formatter AND hints — return early]
     └─ otherwise → OutputFormatterFactory.GetFormatter(format)
                     .FormatQueryResults(result) → formatted string
                     → Console.WriteLine(output)
  9. Emit hints (HintEngine suppresses for json/minimal; ids never reaches here)
```

### Design Decisions

| ID | Decision | Rationale | Rejected Alternative |
|----|----------|-----------|----------------------|
| DD-01 | Server-side `$top` via new `QueryByWiqlAsync` overload | Efficient, avoids 200-ID ceiling — see §DD-01 detail | Client-side truncation (wasteful, silently caps at ~200) |
| DD-02 | WIQL builder as a pure static function in Domain layer | No ADO dependency, fully testable with unit tests, reusable by future commands | *Builder in Infrastructure*: Couples WIQL construction to ADO client. Harder to unit test without HTTP mocks. |
| DD-03 | No async rendering (IAsyncRenderer) for v1 | Query results are fetched all-at-once, not streamed. A loading message + sync table is simpler and sufficient. | *Progressive rendering*: Render results as they stream in. Adds complexity with no UX benefit since WIQL returns IDs in a single batch. |
| DD-04 | "ids" output handled in QueryCommand, not as a new formatter | Adding a 5th formatter for a single use case adds complexity. A simple `string.Join("\n", ids)` in the command is cleaner. | *IdsOutputFormatter class*: Would require modifying `OutputFormatterFactory` and adding a new class for ~3 lines of logic. Over-engineered. |
| DD-05 | AND logic for all filters | Matches user mental model ("show me Tasks that are Doing AND assigned to me"). OR logic across the same filter dimension would require array parameters — deferred. | *OR logic*: Would require `--type Bug --type Task` array syntax. Not supported by ConsoleAppFramework's argument model without custom parsing. |
| DD-06 | `FormatQueryResults` added to `IOutputFormatter` interface; `MinimalOutputFormatter` gets explicit stub | Consistent with existing interface pattern — the one-line stub is explicit and auditable. | *Default interface method (`=> string.Empty`)*: Would avoid touching `MinimalOutputFormatter` but introduces implicit silent behavior and breaks the pattern — no other `IOutputFormatter` methods use default implementations. |
| DD-07 | Default area path applied when no --area-path flag | Prevents accidentally querying the entire project when a user has a configured team scope. Matches RefreshCommand behavior. | *No default scoping*: Queries the entire project by default. Produces noisy results and doesn't match established RefreshCommand behavior. |
| DD-08 | ORDER BY ChangedDate DESC as default sort | Most useful default — users typically want recently-touched items first. | *ORDER BY System.Id ASC*: Stable ordering but less useful for discovery. Recently-changed items are more relevant during triage. |
| DD-09 | `IsTruncated` = `items.Count >= top` (best-effort heuristic) | Standard paginated-API pattern; false positives are negligible — see §DD-09 detail | Omit truncation indicator (loses useful UX signal) |
| DD-10 | Zero-filter queries permitted (scoped by defaults + `--top`) | Enables "show me recent items" workflows — see §DD-10 detail | Require at least one filter (prevents valid workflows) |

#### DD-01: Server-Side Result Limiting via `$top`

The ADO WIQL REST API natively supports a `$top` query parameter
(`POST _apis/wit/wiql?$top=N`). Server-side limiting is more efficient than fetching
all IDs and truncating client-side: it reduces network payload, avoids the ~200-ID
default ceiling, and ensures the user gets exactly N results.

A new `QueryByWiqlAsync(string wiql, int top, CancellationToken ct)` overload is
added to `IAdoWorkItemService`. The existing `(string, CancellationToken)` overload
remains unchanged — all current callers are unaffected.

**Note**: WIQL syntax does not support `SELECT TOP N` — `$top` is a REST API query
parameter, not a WIQL clause.

**Rejected**: *Client-side truncation* — fetch all IDs (up to ~200 default), then
take first N. Simpler (no interface change) but wasteful: fetches 200 IDs when the
user requests 5. The 200-ID default ceiling also means `--top 500` silently returns
≤200 results.

#### DD-09: Truncation Heuristic

The ADO `$top` API returns at most N IDs with no total-count field.
`IsTruncated` is computed as `items.Count >= top` — if the server returned exactly
as many IDs as requested, there are likely more matches. This is the standard pattern
for paginated APIs without a total count header.

**Trade-off**: It is possible (but statistically unlikely) for the exact match count
to equal `top`, producing a false-positive truncation indicator. This is acceptable
because the indicator is informational only ("Showing 25 of 25+ results") and the
cost of a false positive is negligible.

#### DD-10: Zero-Filter Queries

Users may intentionally want to browse recent items: `twig query --top 10` shows the
10 most recently changed items in their team scope. Requiring at least one filter
would prevent this legitimate use case and break the "all filters are optional"
mental model.

The `--top 25` default and default area path scoping (DD-07) prevent unbounded
queries. If no area paths are configured, the query is project-wide — this is
intentional and equivalent to the ADO web UI's default query scope.

## Alternatives Considered

Significant design alternatives are documented inline in the Design Decisions table
(DD-01 through DD-10), each with a "Rejected Alternative" column explaining the
trade-off. Key alternatives evaluated:

- **DD-01**: Client-side truncation vs. server-side `$top` — server-side chosen for
  efficiency and to avoid the 200-ID ceiling
- **DD-02**: WIQL builder in Infrastructure vs. Domain — Domain chosen for testability
  and separation of concerns
- **DD-04**: Dedicated `IdsOutputFormatter` vs. inline `string.Join` — inline chosen
  to avoid over-engineering a 3-line operation
- **DD-06**: Default interface method vs. explicit stub in `MinimalOutputFormatter` —
  explicit stub chosen for consistency with the existing interface pattern

## Dependencies

### External Dependencies
- ADO REST API `_apis/wit/wiql` endpoint (api-version 7.1) — already integrated
- ADO `$top` query parameter — documented, no version change required
- No new NuGet packages required

### Internal Dependencies
- `IAdoWorkItemService.QueryByWiqlAsync()` — existing overload unchanged; new overload added
- `IAdoWorkItemService.FetchBatchAsync()` — existing, no changes
- `IWorkItemRepository.SaveBatchAsync(IEnumerable<WorkItem>, CancellationToken)` — existing, no changes
- `IOutputFormatter` — extended with new method
- `TwigConfiguration.Defaults` — existing, read for default area paths
- `HintEngine` — extended with query-specific hints

### Sequencing Constraints
- Issue 1 (Domain layer) must complete before Issue 2 (CLI layer)
- Issue 3 (Formatting) depends on Issue 1 (QueryResult type) and can be *developed*
  in parallel with Issue 2 on a separate branch. However, Issues 2 and 3 are bundled
  into the same PR group (PR Group 2) because the formatter additions are driven by
  and tested through the command — reviewing them together provides better context.
- Issue 4 (Tests) depends on Issues 1–3

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `IAdoWorkItemService` | New overload: `QueryByWiqlAsync(string, int, CancellationToken)` | Low — additive overload, existing callers unaffected |
| `AdoRestClient` | Implements new overload with `$top` URL parameter | Low — self-contained addition |
| `IOutputFormatter` | New method added | Low — additive change, all 4 implementations updated atomically in the same PR. No external implementations known. `IOutputFormatter` is an interface (not abstract class); all methods are interface members. |
| `TwigCommands` (Program.cs) | New `Query` method | Low — additive, no existing method affected |
| `CommandRegistrationModule` | New `AddSingleton<QueryCommand>()` | Low — additive |
| `HintEngine` | New `"query"` case | Low — additive |
| `TwigJsonContext` | Verification checkpoint — likely no new `[JsonSerializable]` attributes needed (Q-2) | Low — no-op if `Utf8JsonWriter` covers all JSON output |

### Backward Compatibility
- No existing commands are modified
- `IAdoWorkItemService` gains a new overload (not a signature change); all existing
  callers continue using the original `(string, CancellationToken)` overload
- `IOutputFormatter` gains a new method (`FormatQueryResults`); all 4 built-in
  implementations (`HumanOutputFormatter`, `JsonOutputFormatter`,
  `JsonCompactOutputFormatter`, `MinimalOutputFormatter`) are updated atomically
  in the same PR. No known external implementations exist.
- No database schema changes (reuses existing `work_items` table)
- New command is purely additive

### Performance
- Network: 2 ADO API calls per query (WIQL + batch fetch) — same pattern as `twig refresh`
- Server-side `$top` ensures only the requested number of IDs are returned, avoiding
  unnecessary data transfer for small `--top` values
- Cache write: O(N) where N = `--top` (batch insert)
- No impact on existing command performance

## Security Considerations

### WIQL Injection Prevention

User-supplied filter values (`--type`, `--state`, `--assigned-to`, `--area-path`,
`--iteration-path`, and the free-text search argument) are interpolated into WIQL
query strings. This crosses a **security boundary** — unsanitized input could alter
query semantics or extract unintended data.

**Mitigation**: All string values are sanitized via `WiqlQueryBuilder.EscapeWiql()`
which escapes single quotes (`'` → `''`). This matches the escaping pattern already
used by `RefreshCommand` (L73, L83, L102) and `InitCommand` (L335, L343, L355). WIQL uses
single-quoted string literals exclusively; escaping single quotes prevents a user
from breaking out of a string literal and injecting arbitrary WIQL clauses.

**Test coverage**: The `WiqlQueryBuilderTests` suite (Task 1.4) includes dedicated
injection-prevention tests:
- Single quotes in search text (e.g., `O'Brien`)
- Single quotes in filter values (e.g., `--state "Won't Fix"`)
- Multiple quotes and edge cases
- Unicode and special character handling

**Limitations**: WIQL injection is constrained by WIQL's limited syntax — it supports
only `SELECT`, `FROM`, `WHERE`, `ORDER BY`, and `ASOF` clauses, with no DML or DDL
capabilities. An attacker who bypasses escaping could at most broaden/narrow query
results, not modify or delete data. The ADO REST API enforces its own server-side
permissions, so unauthorized data access is not possible even with crafted WIQL.

## Risks and Mitigations

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R-1 | WIQL injection via user-supplied filter values | Medium | High | Single-quote escaping in `WiqlQueryBuilder.EscapeWiql()`, matching existing RefreshCommand pattern. Comprehensive test coverage for edge cases. See Security Considerations section. |
| R-2 | Large result sets overwhelming terminal output | Low | Low | Default `--top 25`. Server-side `$top` ensures only N IDs returned. Clear truncation indicator in output. |
| R-3 | ADO WIQL query timeout for broad queries | Low | Medium | The existing exception pipeline handles timeouts: `AdoErrorHandler` (Infrastructure) throws typed exceptions, which propagate to `ExceptionHandler` (Program.cs) to produce actionable stderr messages. Users can narrow filters. |
| R-4 | `IOutputFormatter` interface change breaks custom formatters | Very Low | Low | No known external implementations. All 4 built-in formatters updated atomically. |
| R-5 | `QueryCommand` coupling to `TwigConfiguration.Defaults.AreaPathEntries` internals | Low | Low | This follows the established pattern in `RefreshCommand` (L77–105), which already reads `AreaPathEntries` from the command layer. The coupling is contained: `QueryCommand` maps `AreaPathEntry` values into `QueryParameters.DefaultAreaPaths` tuples (preserving both `Path` and `IncludeChildren`), keeping the domain layer clean. If config structure changes, only the mapping in `QueryCommand` (and `RefreshCommand`) needs updating. |

## Open Questions

| ID | Question | Status | Resolution |
|----|----------|--------|------------|
| Q-1 | Should `DefaultAreaPaths` on `QueryParameters` carry the `IncludeChildren` flag from `AreaPathEntry`, or always use `UNDER`? | ✅ Resolved | Respect `IncludeChildren` for parity with `RefreshCommand` (L84). `DefaultAreaPaths` is `IReadOnlyList<(string Path, bool IncludeChildren)>?` — the builder emits `UNDER` when true, `=` when false. |
| Q-2 | Does the JSON output for query results require new `[JsonSerializable]` attributes in `TwigJsonContext`? | ✅ Resolved | No. `JsonOutputFormatter` uses `Utf8JsonWriter` for manual JSON construction (matching all other formatter methods). No new serializable types are introduced. Add attributes only if a new serializable type is inadvertently introduced during implementation. |

All open questions have been resolved. No blocking questions remain.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/QueryParameters.cs` | Immutable value object encapsulating all query filter criteria. `DefaultAreaPaths` is `IReadOnlyList<(string Path, bool IncludeChildren)>?` (preserves per-entry scoping from config) |
| `src/Twig.Domain/Services/WiqlQueryBuilder.cs` | Pure-function WIQL string builder from QueryParameters |
| `src/Twig.Domain/ReadModels/QueryResult.cs` | Read model carrying query results with metadata |
| `src/Twig/Commands/QueryCommand.cs` | CLI command implementation |
| `tests/Twig.Domain.Tests/Services/WiqlQueryBuilderTests.cs` | Unit tests for WIQL generation |
| `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | Command-level tests with mocks |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add new overload: `QueryByWiqlAsync(string wiql, int top, CancellationToken ct)` (~1 line) |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Implement new overload with `$top` query parameter (~10 lines) |
| `src/Twig/Program.cs` | Add `Query` method to `TwigCommands` class (~10 lines) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `QueryCommand` in `AddCoreCommands()` (~1 line) |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatQueryResults(QueryResult result)` method (~1 line). Also fix doc comment: update "Three implementations" to "Four implementations" (Human, JSON, JsonCompact, Minimal). |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Add explicit `return string.Empty;` stub for `FormatQueryResults` (~1 line) |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatQueryResults()` table rendering (~60 lines) |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatQueryResults()` JSON output (~40 lines); verify TwigJsonContext needs no new attributes (Q-2) |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Implement `FormatQueryResults()` delegating to full JSON (~5 lines) |
| `src/Twig/Hints/HintEngine.cs` | Add `"query"` case with contextual hints (~10 lines) |

## ADO Work Item Structure

> **Effort key** (applies to all Issue task tables below):
> S = small (< 1 hr), M = medium (2–4 hrs), L = large (> 4 hrs)

### Issue 1: WIQL Query Builder & Parameter Types

**Goal**: Create the domain-layer foundation — `QueryParameters` value object,
`WiqlQueryBuilder` service, and `QueryResult` read model — with comprehensive unit
test coverage.

**Prerequisites**: None (no dependencies on other Issues)

**Tasks**:

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| 1.1 | Create `QueryParameters` sealed record with all filter properties, `Top` default of 25, and `DefaultAreaPaths` as `IReadOnlyList<(string Path, bool IncludeChildren)>?` — preserving per-entry scoping from config (Q-1 resolved: respect `IncludeChildren` for parity with `RefreshCommand`). | `src/Twig.Domain/ValueObjects/QueryParameters.cs` | S | FR-01–FR-10, FR-14, FR-17 |
| 1.2 | Create `WiqlQueryBuilder` static class: `Build(QueryParameters)` method that constructs a `SELECT [System.Id] FROM WorkItems WHERE ...` string with CONTAINS for title, = for type/state/assignee, UNDER for explicit path flags, UNDER or = for default area paths based on each entry's `IncludeChildren` flag (matching `RefreshCommand` L84), >= @Today-N for time filters, default area path fallback, ORDER BY ChangedDate DESC, and EscapeWiql helper. Note: `Top` is NOT embedded in the WIQL — it is passed via the ADO API `$top` parameter. | `src/Twig.Domain/Services/WiqlQueryBuilder.cs` | M | FR-01–FR-08, FR-14, FR-15, FR-17, FR-18, G-7, NFR-02 |
| 1.3 | Create `QueryResult` sealed record with `Items` (`IReadOnlyList<WorkItem>`) and `IsTruncated` (`bool`) properties. `IsTruncated` is computed as `items.Count >= top` — a best-effort heuristic since the ADO `$top` API returns no total count. `TotalMatchCount` is omitted because it would always equal `Items.Count`. | `src/Twig.Domain/ReadModels/QueryResult.cs` | S | G-4 |
| 1.4 | Unit tests for `WiqlQueryBuilder`: title-only, single filter, combined filters, time filters, area path defaults with both `IncludeChildren=true` (UNDER) and `IncludeChildren=false` (=), WIQL escaping (injection prevention), empty parameters | `tests/Twig.Domain.Tests/Services/WiqlQueryBuilderTests.cs` | M | FR-01–FR-08, FR-14, FR-18 |

**Acceptance Criteria**:
- [ ] `WiqlQueryBuilder.Build()` generates valid WIQL for all filter combinations
- [ ] Default area paths respect `IncludeChildren` flag (UNDER when true, = when false)
- [ ] Single-quote characters in filter values are escaped to prevent WIQL injection
- [ ] All domain types are sealed records/classes
- [ ] All tests pass with `dotnet test`

---

### Issue 2: Query Command Core Implementation

**Goal**: Create the `QueryCommand` class, wire it into DI and the `TwigCommands` router,
add the `QueryByWiqlAsync` overload with `$top` support to the ADO service interface and
implementation, and implement the end-to-end query execution flow (WIQL build → ADO query
with `$top` → batch fetch → cache → output).

**Prerequisites**: Issue 1 (WIQL builder and parameter types)

**Tasks**:

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| 2.1 | Add new overload `QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default)` to `IAdoWorkItemService` interface | `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | S | DD-01, FR-10 |
| 2.2 | Implement the new overload in `AdoRestClient`: append `&$top={top}` to the WIQL API URL. Reuse existing deserialization logic. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | S | DD-01, FR-10 |
| 2.3 | Create `QueryCommand` sealed class with full execution flow (§4): parse time filters via private `ParseDuration` static helper → build `QueryParameters` → WIQL → query with `$top` → fetch → cache → format/output → hints → telemetry. On invalid time filter input (`ParseDuration` returns null), write error to stderr and return exit code 1 (FR-20). The "ids" output branch skips formatter and hints (DD-04, FR-19). | `src/Twig/Commands/QueryCommand.cs` | L | G-1–G-6, FR-01–FR-21, NFR-01, NFR-03–NFR-06 |
| 2.4 | Add `Query` method to `TwigCommands` class. Signature: `Query([Argument] string? searchText = null, string? type = null, string? state = null, string? assignedTo = null, string? areaPath = null, string? iterationPath = null, string? createdSince = null, string? changedSince = null, int top = 25, string output = "human", CancellationToken ct = default)`. The `searchText` argument is **optional positional** (`[Argument] string? searchText = null`) — matching the established pattern in `NavDown` (L333: `[Argument] string? idOrPattern = null`). This allows both `twig query 'keyword'` and `twig query --state Doing` (no search text). **CLI flag naming**: ConsoleAppFramework auto-maps camelCase parameters to kebab-case CLI flags: `assignedTo` → `--assigned-to`, `areaPath` → `--area-path`, `iterationPath` → `--iteration-path`, `createdSince` → `--created-since`, `changedSince` → `--changed-since`. | `src/Twig/Program.cs` | S | G-1–G-5 |
| 2.5 | Register `QueryCommand` as singleton in `CommandRegistrationModule.AddCoreCommands()` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S | — |
| 2.6 | Add `"query"` case to `HintEngine.GetHints()`: suggest `twig set <id>` for navigation, `twig show <id>` for peek, `--output ids` for piping | `src/Twig/Hints/HintEngine.cs` | S | NFR-05 |

**Acceptance Criteria**:
- [ ] `twig query 'keyword'` executes a WIQL query and prints results
- [ ] `twig query --state Doing --type Issue` combines filters with AND logic
- [ ] `twig query --output ids` prints one ID per line
- [ ] `twig query --top 5` limits results server-side to 5 items via `$top`
- [ ] `twig query` with no filters returns recently changed items (DD-10)
- [ ] Invalid `--changed-since` value produces error message and exit code 1 (FR-20)
- [ ] Results are cached in SQLite for `twig show` access
- [ ] Telemetry event emitted with command, duration, result_count (no PII)
- [ ] Network errors produce actionable error messages
- [ ] Hints are suppressed for `--output ids` and `--output json` (FR-19)

---

### Issue 3: Query Output Formatting

**Goal**: Extend the `IOutputFormatter` interface with `FormatQueryResults()` and
implement it in all 4 formatter classes. `MinimalOutputFormatter` gets an explicit
`return string.Empty;` stub (NG-7).

**Prerequisites**: Issue 1 (QueryResult read model)

**Tasks**:

> **Note on MinimalOutputFormatter**: Updated in Task 3.1 with an explicit
> `return string.Empty;` stub. Minimal format is out of scope per NG-7.

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| 3.1 | Add `FormatQueryResults(QueryResult result)` to `IOutputFormatter` interface. Fix doc comment from "Three implementations" to "Four implementations" (Human, JSON, JsonCompact, Minimal). Add explicit `return string.Empty;` stub in `MinimalOutputFormatter` (NG-7). | `src/Twig/Formatters/IOutputFormatter.cs`, `src/Twig/Formatters/MinimalOutputFormatter.cs` | S | DD-06 |
| 3.2 | Implement `FormatQueryResults` in `HumanOutputFormatter`: render summary line ("Found N items"), table with columns (ID, Type badge + name, Title, State with color, AssignedTo), truncation warning if applicable | `src/Twig/Formatters/HumanOutputFormatter.cs` | M | FR-11, NFR-05 |
| 3.3 | Implement `FormatQueryResults` in `JsonOutputFormatter`: JSON object with `query` description, `count` (= `items.Count`), `truncated` boolean, `items` array (each with id, type, title, state, assignedTo, areaPath, iterationPath) using manual `Utf8JsonWriter`. While implementing, confirm no new `[JsonSerializable]` attributes are needed in `TwigJsonContext` (Q-2 resolved: `Utf8JsonWriter` handles output without source-gen; add attributes only if a new serializable type was inadvertently introduced). | `src/Twig/Formatters/JsonOutputFormatter.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | M | FR-12, NFR-01 |
| 3.4 | Implement `FormatQueryResults` in `JsonCompactOutputFormatter`: delegate to `JsonOutputFormatter` (consistent with existing pattern) | `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | S | FR-12 |

**Acceptance Criteria**:
- [ ] Human format renders a readable table with type badges and state colors
- [ ] JSON format produces valid, parseable JSON with stable schema (`count`, not `totalCount`)
- [ ] `MinimalOutputFormatter` has explicit `return string.Empty;` stub for `FormatQueryResults` (NG-7)
- [ ] Truncation indicator shows when results were limited by `--top`
- [ ] All formatters compile and pass build with `TreatWarningsAsErrors`
- [ ] Zero-result case shows "No items found" message
- [ ] `TwigJsonContext` verified: no new `[JsonSerializable]` attributes needed (Task 3.3)

---

### Issue 4: Query Command Tests

**Goal**: Comprehensive test coverage for the query command — mock-based CLI tests
exercising all parameters, output formats, error paths, and edge cases.

**Prerequisites**: Issues 1–3 (all production code complete)

> **TestKit reference**: Tests use `WorkItemBuilder` from the shared test infrastructure
> project at `tests/Twig.TestKit/WorkItemBuilder.cs`. This fluent builder creates
> `WorkItem` instances with sensible defaults so tests only need to set relevant
> properties (e.g., `new WorkItemBuilder(42, "Title").InState("Doing").Build()`).
> The `Twig.TestKit` project is referenced by all test projects.

**Tasks**:

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| 4.1 | Create `QueryCommandTests` with NSubstitute mocks for IAdoWorkItemService, IWorkItemRepository, TwigConfiguration, OutputFormatterFactory, HintEngine | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | — |
| 4.2 | Test: keyword search — verify WIQL contains `CONTAINS 'keyword'`, verify results rendered | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-01 |
| 4.3 | Test: combined filters — verify WIQL contains all filter clauses with AND | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-02–FR-06, FR-14 |
| 4.4 | Test: time filters — verify `--changed-since 7d` produces `@Today - 7` in WIQL | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-07, FR-08, FR-09 |
| 4.5 | Test: IDs output — verify one ID per line, no formatter called, hints suppressed | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-13, FR-19 |
| 4.6 | Test: zero results — verify friendly message, exit code 0 | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | NFR-03, NFR-05 |
| 4.7 | Test: results cached — verify `SaveBatchAsync(IEnumerable<WorkItem>, CancellationToken)` called with fetched items | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-16 |
| 4.8 | Test: default area path applied from config when no --area-path | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-17 |
| 4.9 | Test: `$top` passed to `QueryByWiqlAsync` overload — verify the `top` parameter reaches the ADO service | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-10, DD-01 |
| 4.10 | Test: invalid `--changed-since` value — verify error message format, exit code 1, no WIQL query executed | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-20 |
| 4.11 | Test: no filters — verify query executes with only default area paths and `ORDER BY`, results returned | `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | S | FR-21, DD-10 |

**Acceptance Criteria**:
- [ ] All tests pass with `dotnet test`
- [ ] Happy path, edge cases, and error paths covered
- [ ] Invalid time filter input produces actionable error (FR-20)
- [ ] No-filter query executes successfully with defaults (FR-21)
- [ ] WIQL injection prevention tested in `WiqlQueryBuilderTests` (Task 1.4) — not duplicated here
- [ ] Tests use `WorkItemBuilder` from TestKit (`tests/Twig.TestKit/WorkItemBuilder.cs`) for test data
- [ ] Tests follow existing naming convention: `MethodName_Scenario_Expected`
- [ ] No test depends on network or file system

---

## PR Groups

### PR Group 1: Domain Foundation

**Tasks**: 1.1, 1.2, 1.3, 1.4  
**Classification**: Deep (few files, algorithmic logic)  
**Estimated LoC**: ~340 (3 production files + 1 test file)  
**Files**: ~4  
**Successors**: PR Group 2  

**Description**: Introduces the domain-layer query building infrastructure. All new files
in `Twig.Domain`, no modifications to existing code. Self-contained and independently
testable.

---

### PR Group 2: Query Command, CLI Integration, Output Formatting & Tests

**Tasks**: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 3.1, 3.2, 3.3, 3.4, 4.1–4.11  
**Classification**: Deep (few files, integration logic + mechanical formatter additions + test coverage)  
**Estimated LoC**: ~880 (AdoRestClient overload ~10, QueryCommand ~200, Program.cs ~15, DI ~5, Hints ~15, formatters ~130, TwigJsonContext ~2, CLI tests ~450, IAdoWorkItemService ~1, minor domain test additions ~50)  
**Files**: ~15 modified + 2 new  
**Predecessors**: PR Group 1  

> **Size note**: At ~880 LoC this PR is large but within the ≤2000 LoC budget. The
> sub-grouping below provides clear reviewer navigation. Splitting into smaller PRs
> was considered but rejected: the formatter additions have no standalone callers
> outside `QueryCommand`, and shipping `IOutputFormatter` extensions without their
> caller would produce dead code in intermediate PRs. The tests must ship with the
> production code they cover.

**Sub-grouping for reviewer navigation:**
- **ADO interface & implementation** (Tasks 2.1–2.2): `IAdoWorkItemService` overload + `AdoRestClient` impl
- **Command & wiring** (Tasks 2.3–2.6): `QueryCommand`, `Program.cs`, DI registration, hints
- **Formatting** (Tasks 3.1–3.4): `IOutputFormatter` extension + 4 formatter impls (MinimalOutputFormatter explicit stub) + JSON context verification (inline in 3.3)
- **Tests** (Tasks 4.1–4.11): `QueryCommandTests` — all mock-based CLI tests  

**Description**: Adds `$top`-aware `QueryByWiqlAsync` overload to the ADO service
interface and implementation, implements the core query command, wires DI and routing,
adds hints, extends all 4 formatters with `FormatQueryResults` (MinimalOutputFormatter
gets an explicit `return string.Empty;` stub per DD-06/NG-7), and ships comprehensive
command-level tests. The formatter additions are driven by and tested through
`QueryCommand` — they have no standalone callers outside this feature. Splitting them
into a separate PR would produce dead code or require stubs. Bundling keeps the full
call chain visible in one diff, and ensures production code never ships without coverage.
WIQL injection prevention is tested in Task 1.4 (WiqlQueryBuilderTests) and not
duplicated in the command tests. TwigJsonContext verification is folded into Task 3.3.

---

## References

- [ADO WIQL REST API Reference](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/wiql/query-by-wiql) — documents `$top` query parameter
- [WIQL Syntax Reference](https://learn.microsoft.com/en-us/azure/devops/boards/queries/wiql-syntax) — confirms no `SELECT TOP N` syntax support
- Existing WIQL usage: `src/Twig/Commands/RefreshCommand.cs` L71–108
- Existing WIQL usage: `src/Twig/Commands/InitCommand.cs` L334–361
- Formatter pattern: `src/Twig/Formatters/IOutputFormatter.cs`
- Command pattern: `src/Twig/Commands/ShowCommand.cs`
- TestKit builder: `tests/Twig.TestKit/WorkItemBuilder.cs`
