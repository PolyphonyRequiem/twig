# Query Command UX Improvements

> **Status**: ✅ Done
> **Issue**: #1638 (Epic)

## Executive Summary

This plan delivers four UX improvements to the `twig query` command: (1) showing a helpful summary when invoked with no arguments instead of silently running a broad query, (2) splitting the single `searchText` positional argument into separate `--title` and `--description` filter flags for precise field-level searching, (3) adding structured query support with a `--filter` flag that accepts compound expressions like `state:Doing AND type:Bug`, and (4) improving discoverability through richer help text, contextual hints, and usage examples. These changes are backward-compatible — the existing positional `searchText` argument continues to work as before, while new flags provide finer-grained control. Modifications span the CLI layer (`QueryCommand.cs`, `Program.cs`), the domain layer (`QueryParameters`, `WiqlQueryBuilder`), formatter updates, and comprehensive tests.

## Background

### Current Architecture

The `twig query` command is the primary ad-hoc search interface for work items. Its data flow is:

```
CLI flags → QueryCommand.ExecuteAsync()
         → QueryParameters (value object)
         → WiqlQueryBuilder.Build() (static WIQL generator)
         → IAdoWorkItemService.QueryByWiqlAsync() (ADO REST)
         → IAdoWorkItemService.FetchBatchAsync() (hydrate full items)
         → IWorkItemRepository.SaveBatchAsync() (cache in SQLite)
         → IOutputFormatter.FormatQueryResults() (render)
         → HintEngine.GetHints("query") (contextual tips)
```

**Current CLI Signature** (`TwigCommands.Query()` in `Program.cs`):
```csharp
public async Task<int> Query(
    [Argument] string? searchText = null,    // positional, searches title+description
    string? type = null,                      // --type
    string? state = null,                     // --state
    string? assignedTo = null,                // --assignedTo
    string? areaPath = null,                  // --areaPath
    string? iterationPath = null,             // --iterationPath
    string? createdSince = null,              // --createdSince (Nd/Nw/Nm)
    string? changedSince = null,              // --changedSince (Nd/Nw/Nm)
    int top = 25,                             // --top
    string output = OutputFormatterFactory.DefaultFormat,  // --output
    CancellationToken ct = default)
```

This method currently has 10 parameters plus `CancellationToken` (11 total). The highest parameter count among other commands is `TwigCommands.New()` with 9 parameters (title, type, area, iteration, description, parent, set, editor, output) plus `CancellationToken` and a `params string[] titleParts` array (11 total).

**Current search behavior**: The `searchText` parameter generates a WIQL `CONTAINS` clause that searches **both** `System.Title` and `System.Description` simultaneously via an OR group:
```wiql
([System.Title] CONTAINS 'keyword' OR [System.Description] CONTAINS 'keyword')
```

**No-arguments behavior**: When `twig query` is called with zero arguments, all parameters default to null (except `top=25`). The command silently executes a query scoped only by default area paths from config. This produces a broad result set with no user feedback about what was searched.

**Output formats**: `OutputFormatterFactory` supports five format names — `human` (default), `json` (aliases: `json-full`), `json-compact`, and `minimal` — plus the special `ids` format handled inline at `QueryCommand.ExecuteCoreAsync()` before the formatter is resolved.

**IDs format handling**: The `ids` output format is handled specially — it short-circuits before the formatter at the point where results are returned, writing one ID per line to stdout. This occurs at `QueryCommand.ExecuteCoreAsync()` and bypasses both `IOutputFormatter` and `HintEngine` entirely.

**QueryResult data flow**: `BuildQueryDescription()` produces a human-readable filter summary string that is stored as `QueryResult.Query`. This field is consumed by all formatters — `JsonOutputFormatter.FormatQueryResults()` emits it as the `"query"` JSON property, and `HumanOutputFormatter` uses it as a section header. Any changes to `BuildQueryDescription()` to describe new filter types automatically flow through to `QueryResult.Query` without changes to `QueryResult` itself.

### Current Hint Behavior

The `HintEngine` `case "query":` branch in `GetHints()` emits three static hints after every query:
```
Use 'twig set <id>' to navigate to an item.
Use 'twig show <id>' to view item details.
Use '--output ids' to pipe IDs to other commands.
```

These hints are static — they don't adapt to what the user actually did (no-args, keyword search, filter combination). The `GetHints()` method signature currently accepts `commandName`, `item`, `workspace`, `outputFormat`, `newStateName`, `createdId`, `siblings`, and `staleSeedCount` — but no query-specific context (e.g., whether filters were used or results were truncated).

### Call-Site Audit

The `QueryParameters` and `WiqlQueryBuilder` types are used only within the query pipeline — no other commands depend on them. Changes to these types have a contained blast radius.

This audit describes the **current state** of each call site. Proposed changes for each site are detailed in the [Proposed Design](#proposed-design) section.

| File | Method/Site | Current Usage |
|------|-------------|---------------|
| `QueryCommand.ExecuteCoreAsync()` | `new QueryParameters { ... }` | Builds `QueryParameters` from CLI args — sets `SearchText`, `TypeFilter`, `StateFilter`, etc. |
| `QueryCommand.ExecuteCoreAsync()` | `WiqlQueryBuilder.Build(parameters)` | Passes assembled `QueryParameters` to static WIQL builder |
| `QueryCommand.BuildQueryDescription()` | Iterates `QueryParameters` fields | Produces a human-readable string ("title contains 'x' AND state = 'y'") stored as `QueryResult.Query` |
| `QueryCommand.ExecuteCoreAsync()` | `ids` format branch | Short-circuits before formatter: writes bare IDs to stdout, returns `(0, count)` |
| `WiqlQueryBuilder.Build()` | Calls `AppendSearchText()` | Invokes `AppendSearchText(clauses, parameters.SearchText)` as part of clause assembly |
| `WiqlQueryBuilder.AppendSearchText()` | Method definition | Generates `([System.Title] CONTAINS 'x' OR [System.Description] CONTAINS 'x')` from a single `searchText` value |
| `TwigCommands.Query()` in `Program.cs` | CLI parameter binding | Binds CLI flags to `QueryCommand.ExecuteAsync()` — currently 10 params + `CancellationToken` |
| `HintEngine.GetHints()` | `case "query":` branch | Returns 3 static hint strings (set, show, ids) regardless of query context |
| `IOutputFormatter.FormatQueryResults()` | Interface method | Takes `QueryResult`, returns formatted string — implemented by Human, Json, JsonCompact, and Minimal formatters |
| `JsonOutputFormatter.FormatQueryResults()` | Implementation | Uses `Utf8JsonWriter` to produce JSON with `query`, `count`, `truncated`, `items` fields |
| `JsonCompactOutputFormatter.FormatQueryResults()` | Implementation | Uses `Utf8JsonWriter` to produce compact JSON array of items |
| `QueryCommandTests` | 25+ test methods | Covers existing search, filter, and format scenarios |
| `WiqlQueryBuilderTests` | 20+ test methods | Covers WIQL clause generation for all current filter types |

## Problem Statement

The `twig query` command has four UX gaps:

1. **No-argument confusion**: Running `twig query` with no arguments silently executes a broad query. Users don't know what the command does, what filters are available, or whether they need to provide arguments. Unlike other CLI tools that show help or usage when invoked bare, `twig query` just returns a potentially large result set scoped by config defaults.

2. **Imprecise text search**: The `searchText` positional argument always searches both title and description simultaneously. Users who want to find items by title only (e.g., exact titles) or description only (e.g., searching for implementation details) must accept false positives from the other field. There is no way to target a single field.

3. **No compound filters**: Each filter maps to a single WIQL clause. Users who need complex conditions (e.g., "state is Doing OR state is New" or "type is Bug AND assigned to me AND changed this week") must run multiple queries and mentally combine results. There is no expression syntax for compound conditions.

4. **Poor discoverability**: The XML doc comment `"Search and filter work items via ad-hoc WIQL queries"` is the only description. Users don't know about available flags (`--createdSince`, `--changedSince`), supported duration formats (`Nd/Nw/Nm`), or output modes (`--output ids`). The hints are static and don't guide users toward more powerful usage patterns.

## Goals and Non-Goals

### Goals

1. **G1**: When `twig query` is invoked with no arguments, show a helpful summary that includes available filters, usage examples, and default config (area paths) — making the command self-documenting.
2. **G2**: Add `--title` and `--description` flags that generate independent WIQL `CONTAINS` clauses, allowing field-specific search while preserving the existing `searchText` positional behavior.
3. **G3**: Add a `--filter` flag accepting structured expressions (e.g., `--filter "state:Doing AND type:Bug"`) that compile into WIQL WHERE clauses.
4. **G4**: Improve contextual hints after query execution (e.g., suggest `--title` when broad `searchText` is used, suggest `--top` when results are truncated).
5. **G5**: All changes are backward-compatible; existing CLI invocations produce identical results.

### Non-Goals

- **NG1**: Full WIQL passthrough — users will not type raw WIQL. The `--filter` flag provides a simplified expression syntax.
- **NG2**: Local-only querying (searching the SQLite cache without hitting ADO) — this is a separate feature.
- **NG3**: Saved/named queries — out of scope for this iteration.
- **NG4**: Interactive query builder (TUI) — this would be a separate TUI feature.
- **NG5**: Changing the default `searchText` behavior — the existing positional arg continues to search both title and description.

## Requirements

### Functional Requirements

| ID | Requirement | Issue |
|----|-------------|-------|
| FR-01 | `twig query` with no arguments shows a summary with available filters, usage examples, and default area paths | #1639 |
| FR-02 | No-args summary includes all available filter flags with descriptions | #1639 |
| FR-03 | No-args summary shows configured default area paths (if any) | #1639 |
| FR-04 | No-args summary: human/json/json-full/json-compact output the human-readable text, minimal suppresses (exit 0), ids produces empty output (exit 0) | #1639 |
| FR-05 | `--title <text>` flag generates `[System.Title] CONTAINS '<text>'` WIQL clause | #1640 |
| FR-06 | `--description <text>` flag generates `[System.Description] CONTAINS '<text>'` WIQL clause | #1640 |
| FR-07 | `--title` and `--description` can be combined (AND-joined) | #1640 |
| FR-08 | `--title`/`--description` can be combined with existing `searchText` (AND-joined) | #1640 |
| FR-09 | `--filter` flag accepts expressions like `state:Doing`, `type:Bug AND state:New` | #1641 |
| FR-10 | Filter expressions support AND/OR operators with parenthesized grouping | #1641 |
| FR-11 | Filter expressions support the `:` (equals/UNDER) field operator; additional operators (`~`, `>`, `<`) deferred to future iteration based on concrete user demand | #1641 |
| FR-12 | Unsupported filter fields produce a clear error with list of valid fields | #1641 |
| FR-13 | Query hints adapt to context (no-args, broad search, truncated results) | #1642 |
| FR-14 | Help text on the command includes usage examples and filter documentation | #1642 |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | All changes AOT-compatible (no reflection, source-gen JSON) |
| NFR-02 | Backward-compatible: existing `twig query <text>` invocations unchanged |
| NFR-03 | Invalid filter expressions produce actionable error messages with exit code 1 |
| NFR-04 | Telemetry tracks filter usage without leaking field values (safe: `had_title_filter`, `had_description_filter`, `had_filter_expression`) |

## Proposed Design

### Architecture Overview

The design extends the existing query pipeline with three new capabilities:

```
                        ┌───────────────────────┐
                        │   CLI Parameter Layer  │
                        │   (Program.cs)         │
                        ├───────────────────────┤
                        │ searchText (existing)  │
                        │ --title (NEW)          │
                        │ --description (NEW)    │
                        │ --filter (NEW)         │
                        └────────┬──────────────┘
                                 │
                        ┌────────▼──────────────┐
                        │   QueryCommand         │
                        │   (orchestration)      │
                        ├────────────────────────┤
                        │ No-args detection      │
                        │ Filter parsing         │
                        │ Parameter assembly     │
                        └────────┬──────────────┘
                                 │
              ┌──────────────────┼──────────────────┐
              │                  │                   │
    ┌─────────▼──────┐  ┌───────▼────────────┐  ┌──────▼───────────┐
    │ QueryParameters │  │ FilterExpression   │  │ QuerySummary     │
    │ (extended)      │  │ Parser (NEW)       │  │ Renderer (NEW)   │
    │ +TitleFilter    │  │ static class       │  │ No-args help     │
    │ +DescFilter     │  │ Lexer+Parser       │  │ Filter docs      │
    │ +FilterExpr     │  │ → FilterExpr AST   │  │ Config summary   │
    └─────────┬──────┘  └───────┬────────────┘  └──────────────────┘
              │                 │
    ┌─────────▼─────────────────▼──────────┐
    │         WiqlQueryBuilder              │
    │         (static, extended)            │
    │  + AppendTitleFilter()               │
    │  + AppendDescriptionFilter()         │
    │  + AppendFilterExpression()           │
    └──────────────────────────────────────┘
```

### Key Components

#### 1. No-Args Summary (#1639)

When `twig query` is invoked with no arguments and no filters, instead of executing a broad query, the command renders a helpful summary:

```
twig query — Search and filter work items

Usage:
  twig query <search>           Search title and description
  twig query --title <text>     Search by title only
  twig query --state Doing      Filter by state
  twig query --filter "..."     Structured filter expression

Available filters:
  --title         Search in title field (CONTAINS)
  --description   Search in description field (CONTAINS)
  --type          Filter by work item type (exact match)
  --state         Filter by state (exact match)
  --assignedTo    Filter by assignee (exact match)
  --areaPath      Filter by area path (UNDER)
  --iterationPath Filter by iteration path (UNDER)
  --createdSince  Items created within N days/weeks/months (e.g., 7d, 2w, 1m)
  --changedSince  Items changed within N days/weeks/months
  --filter        Structured filter expression (e.g., "state:Doing AND type:Bug")
  --top           Max results (default: 25)
  --output        Output format: human, json, json-full, json-compact, minimal, ids

Defaults:
  Area paths: MyProject\TeamA (include children), MyProject\TeamB

Examples:
  twig query "login bug"                      Search title & description
  twig query --title "API" --state Doing      Title search + state filter
  twig query --filter "type:Bug AND state:New" Structured filter
  twig query --changedSince 7d --top 50       Recently changed items
```

**Detection logic**: No-args is detected when ALL filter parameters are null/default AND no `searchText`, `title`, `description`, or `filter` is provided. The `--output` and `--top` flags alone do NOT suppress the summary (they are formatting/limit controls, not filters).

**Implementation**: Add a `RenderQuerySummary()` private method to `QueryCommand` that produces the summary text. It branches on the `output` parameter:
- **human / json / json-full / json-compact**: Renders the human-readable summary with Spectre.Console markup — flag list, configured area paths, examples. The `--output` flag controls result formatting, not help text; there are no results to format here.
- **minimal**: Suppresses output (return exit code 0).
- **ids**: Produces empty output (exit code 0) — there are no results to emit IDs for, and `ids` format is designed for piping into other commands. This is consistent with how `ids` is handled elsewhere: it short-circuits before the formatter.

Keeping `RenderQuerySummary()` inline in the command avoids polluting `IOutputFormatter` with a method that only one command uses. No `FormatQuerySummary()` method will be added to `IOutputFormatter`.

#### 2. Separate Title/Description Filters (#1640)

Add two new parameters to `QueryCommand.ExecuteAsync()`:

```csharp
public async Task<int> ExecuteAsync(
    string? searchText = null,      // existing: searches title OR description
    string? title = null,           // NEW: searches title only
    string? description = null,     // NEW: searches description only
    string? type = null,
    // ... rest unchanged
```

**QueryParameters** extended with:
```csharp
public string? TitleFilter { get; init; }
public string? DescriptionFilter { get; init; }
```

**WiqlQueryBuilder** changes:
- Existing `AppendSearchText()` unchanged (backward-compatible)
- New `AppendTitleFilter()`: `[System.Title] CONTAINS '<text>'`
- New `AppendDescriptionFilter()`: `[System.Description] CONTAINS '<text>'`
- All three can coexist (AND-joined): `searchText` produces `(Title CONTAINS OR Description CONTAINS)`, while `--title` and `--description` produce separate targeted clauses

**Interaction matrix**:
| searchText | --title | --description | WIQL Result |
|-----------|---------|---------------|-------------|
| "bug" | null | null | `(Title CONTAINS 'bug' OR Description CONTAINS 'bug')` |
| null | "API" | null | `Title CONTAINS 'API'` |
| null | null | "impl" | `Description CONTAINS 'impl'` |
| null | "API" | "impl" | `Title CONTAINS 'API' AND Description CONTAINS 'impl'` |
| "bug" | "API" | null | `(Title CONTAINS 'bug' OR Description CONTAINS 'bug') AND Title CONTAINS 'API'` |

**BuildQueryDescription changes**: Add branches for `TitleFilter` and `DescriptionFilter` — e.g., `"title contains 'API'"`, `"description contains 'impl'"`. These flow through to `QueryResult.Query` automatically.

**Drive-by fix**: `BuildQueryDescription()` currently describes `searchText` as `"title contains 'keyword'"`, but `WiqlQueryBuilder.AppendSearchText()` actually searches **both** title and description via OR. Update the description to `"title or description contains 'keyword'"` to match the actual WIQL behavior. This is a pre-existing inaccuracy that should be corrected alongside the new filter descriptions.

#### 3. Structured Filter Expressions (#1641)

Add a `--filter` parameter that accepts a mini-expression language:

```
twig query --filter "state:Doing AND type:Bug"
twig query --filter "assignedTo:\"Daniel Green\""
twig query --filter "(state:Doing OR state:New) AND type:Task"
```

**Expression Grammar**:

This grammar is **deliberately minimal** — it covers the most common filter patterns with a small operator set. It is designed to be **extensible**: new fields can be added to the allowlist and new operators can be introduced without changing the grammar structure. However, each new operator increases the maintenance and documentation burden, so additions should be justified by concrete user demand.

```
expression  = term (("AND" | "OR") term)*
term        = "(" expression ")" | comparison
comparison  = field ":" value
field       = "state" | "type" | "assignedTo" | "areaPath" | "iterationPath"
value       = quoted_string | unquoted_word
```

Only the `:` (equals) operator is supported. `title`/`description` fields are intentionally excluded — use the dedicated `--title`/`--description` flags (added in #1640). `createdSince`/`changedSince` fields are intentionally excluded — use the dedicated `--createdSince`/`--changedSince` flags. Both sets of dedicated flags are AND-joined with any `--filter` clauses. Additional operators and fields can be added in a future iteration based on concrete user demand.

**Operator mapping to WIQL**:
| Operator | Meaning | Applicable Fields | WIQL |
|----------|---------|-------------------|------|
| `:` | Equals (or UNDER for path fields) | `state`, `type`, `assignedTo`, `areaPath`, `iterationPath` | `= 'value'` / `UNDER 'value'` |

**Implementation**: A new `FilterExpressionParser` **internal static class** in `Twig.Domain.Services` (mirroring `WiqlQueryBuilder`'s `internal static class` design — the parser is a pure transformation with no dependencies and requires no DI registration):
1. Tokenizes the input string (lexer phase)
2. Parses into a `FilterExpression` AST (parser phase)
3. The AST is stored in `QueryParameters.FilterExpression`
4. `WiqlQueryBuilder` traverses the AST to emit WIQL clauses

**FilterExpression AST types** (in `Twig.Domain.ValueObjects`):
```csharp
public abstract record FilterExpression;
public sealed record ComparisonFilter(string Field, string Value) : FilterExpression;
public sealed record AndFilter(FilterExpression Left, FilterExpression Right) : FilterExpression;
public sealed record OrFilter(FilterExpression Left, FilterExpression Right) : FilterExpression;
```

**Field validation**: The parser validates field names against a known allowlist of 5 supported fields. Unknown fields produce an error that lists only the supported fields and directs users to dedicated flags for excluded fields:
```
error: Unknown filter field 'priority'. Valid fields: state, type, assignedTo, areaPath, iterationPath
Tip: Use --title/--description for text search, --createdSince/--changedSince for date filtering.
```

Fields intentionally excluded from `--filter` (with rationale):
- `title`, `description` → use `--title`/`--description` flags for `CONTAINS` semantics (`:` in `--filter` maps to `=`, which is the wrong operator for text search)
- `createdSince`, `changedSince` → use `--createdSince`/`--changedSince` flags (these require duration-to-date conversion, not direct equality)

**Interaction with other flags**: `--filter` clauses are AND-joined with any other CLI flags. This means `twig query --state Doing --filter "type:Bug"` produces `State = 'Doing' AND Type = 'Bug'`.

**BuildQueryDescription changes**: Add a branch that produces a readable summary of filter expressions — e.g., `"filter: state = 'Doing' AND type = 'Bug'"`. This flows through to `QueryResult.Query` automatically.

#### 4. Discoverability & Documentation (#1642)

**Contextual Hints** — Replace the static hints in the `case "query":` branch of `HintEngine.GetHints()` with context-aware suggestions:

| Context | Hints |
|---------|-------|
| No results | "Try broadening your search: remove filters or use --top to increase the limit." |
| Broad search (searchText used) | "Tip: Use --title or --description for more precise results." |
| Results truncated | "Showing top N results. Use --top to increase, or add filters to narrow." |
| No filters (with results) | "Tip: Add --state, --type, or --assignedTo to filter results." |
| Default (results exist, no specific context matched) | "Use 'twig set <id>' to navigate to an item. Try '--output ids' for scripting." |

The "Default" context replaces the current static hints. It fires when results exist but none of the more specific contexts (broad search, truncated, no filters) apply. This avoids the need for a "first-time query" detection mechanism (which would require tracking query history in SQLite — out of scope per NG2/NG3).

**Context plumbing** — `QueryCommand` passes query context into `HintEngine.GetHints()` via an optional `QueryHintContext?` parameter that conveys: whether searchText/title/description/filter were used, whether results were truncated, and the result count.

**`QueryHintContext` type** (in `src/Twig/Hints/QueryHintContext.cs`):
```csharp
public sealed record QueryHintContext(
    bool UsedSearchText,
    int ResultCount,
    bool IsTruncated);
```

Only these three properties are needed to drive the hint cases: `UsedSearchText` selects the "broad search" hint, `ResultCount == 0` selects the "no results" hint, and `IsTruncated` selects the "truncated" hint.

> **Existing quirk (inherited, not worsened)**: `HintEngine.GetHints()` suppresses hints for `"json"` and `"minimal"` output formats but NOT for `"json-compact"` or `"json-full"`. This is an existing behavior in the codebase. The contextual hints follow the same suppression pattern — fixing the format suppression list is a separate concern.

**XML Doc Comments** — Update the summary on `TwigCommands.Query()` to include examples:
```csharp
/// <summary>
/// Search and filter work items. Use with no arguments for usage help.
/// Examples: twig query "login bug", twig query --title "API" --state Doing
/// </summary>
```

**BuildQueryDescription** — Update `QueryCommand.BuildQueryDescription()` to include title/description/filter in the human-readable summary string. Since `BuildQueryDescription()` feeds into `QueryResult.Query`, these additions automatically propagate to all formatters.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **No-args shows help, not results** | Users expect bare commands to show usage; broad queries are surprising. A `--all` flag can be added later if users want the old behavior. |
| **`searchText` remains positional** | Backward-compatible. `twig query "bug"` continues to work identically. |
| **AST-based filter parsing** | Enables validation, proper operator precedence, and clean WIQL generation without string manipulation hacks. |
| **Field validation at parse time** | Fail fast with helpful error instead of letting invalid WIQL reach ADO and produce cryptic API errors. |
| **AND-join between flag groups** | Consistent with existing behavior — all CLI flags are AND-joined. `--filter` internal operators can use AND/OR. |
| **Filter parser in Domain layer** | The parser produces domain value objects (`FilterExpression`) and doesn't depend on infrastructure. It's a pure transformation. |
| **FilterExpressionParser is static** | Mirrors `WiqlQueryBuilder`'s static design — pure transformation with no dependencies, no DI registration needed. |
| **No-args JSON uses human text** | The `--output` flag controls result formatting, not help text. There are no results to format, so all non-suppressed formats render the same human-readable summary. Adding a separate `Utf8JsonWriter` JSON object for a help screen has no real consumers and adds ~50 LoC for no benefit. |
| **No IOutputFormatter changes for no-args** | `RenderQuerySummary()` is inline in `QueryCommand` — only one command needs it, so adding it to the interface would pollute all 4 formatter implementations. |
| **ids format produces empty output in no-args** | The `ids` format is a piping tool — no results means no IDs. Consistent with the existing short-circuit behavior. |

## Alternatives Considered

### Alt 1: WIQL Passthrough Flag
A `--wiql` flag that accepts raw WIQL WHERE clauses.
- **Pros**: Maximum flexibility, no parser needed.
- **Cons**: Exposes internal WIQL syntax to users; error messages from ADO are cryptic; injection risk if combined with other flags; poor discoverability.
- **Decision**: Rejected — the structured `--filter` provides 90% of the flexibility with much better UX.

### Alt 2: Multiple `--state` Flags Instead of Filter Expressions
Allow `--state Doing --state New` to mean OR.
- **Pros**: Simpler, no parser needed.
- **Cons**: Only works for a single field; doesn't scale to cross-field conditions; ConsoleAppFramework may not support repeated flags well.
- **Decision**: Rejected — `--filter "state:Doing OR state:New"` is more general and handles cross-field conditions.

### Alt 3: No-Args Executes Default Query with Banner
Show a summary header but still execute the default query.
- **Pros**: Users always get results.
- **Cons**: Broad queries can be slow over the network; results without filters are rarely useful; mixes help text with data output.
- **Decision**: Rejected — clean separation of help and query execution is more intuitive.

## Dependencies

### External Dependencies
- **ConsoleAppFramework** — existing dependency, no new features needed
- **Spectre.Console** — existing dependency, used for summary rendering

### Internal Dependencies
- `QueryParameters` (extended with new properties)
- `WiqlQueryBuilder` (extended with new methods)
- `HintEngine` (updated query hints)

### Sequencing Constraints
- Issue #1640 (title/description split) should be implemented before #1641 (structured filters) because the filter expression parser references the same field set
- Issue #1639 (no-args help) and #1642 (discoverability) are independent of the filter changes

## Impact Analysis

### Components Affected
| Component | Type of Change |
|-----------|---------------|
| `QueryParameters` | Additive (3 new properties) |
| `WiqlQueryBuilder` | Additive (3 new methods) + minor refactor |
| `QueryCommand` | Modified (no-args detection, new params, filter parsing, inline summary rendering) |
| `Program.cs:TwigCommands.Query` | Modified (new CLI parameters) |
| `HintEngine` | Modified (contextual query hints) |
| `FilterExpressionParser` | New file |
| `FilterExpression` types | New file |

### Backward Compatibility
- ✅ `twig query "text"` — unchanged behavior
- ✅ `twig query --state Doing` — unchanged behavior
- ✅ `twig query --output json` — unchanged JSON schema (new fields are additive)
- ⚠️ `twig query` (no args) — **behavior change**: shows help instead of running query. This is intentional and documented.

### Performance
- No-args path avoids network call entirely (faster than before)
- Filter expression parsing is a single-pass tokenizer + recursive descent parser — negligible overhead
- All other code paths have identical performance

## Security Considerations

### WIQL Injection Boundary

The `--filter` flag accepts user-controlled input that is compiled into WIQL WHERE clauses. This creates a potential injection boundary:

**Attack surface**: A malicious `--filter` value like `state:Doing' OR 1=1 --` could attempt to inject arbitrary WIQL if values are interpolated unsafely.

**Mitigation strategy** (defense-in-depth):
1. **Parse-time validation**: The `FilterExpressionParser` validates field names against a closed allowlist of 5 fields. Unknown fields are rejected before reaching WIQL generation. The parser tokenizes and parses the input into a typed AST (`FilterExpression` records) — raw strings never pass through to WIQL.
2. **Value escaping**: `WiqlQueryBuilder.EscapeWiqlString()` escapes single quotes in all values before interpolation into WIQL strings. This is the same escaping applied to existing CLI parameters (`--state`, `--type`, etc.) and is the established mitigation pattern in the codebase.
3. **AST-based generation**: WIQL is generated by traversing the typed `FilterExpression` AST, not by string concatenation of raw input. Each `ComparisonFilter` node produces exactly one `field = 'escaped_value'` clause — there is no path from user input to arbitrary WIQL structure.
4. **No raw WIQL passthrough**: The design explicitly rejects a `--wiql` flag (see Alternatives Considered) to avoid exposing the full WIQL attack surface.

**Verification**: T-1641.6 includes explicit WIQL injection prevention tests (e.g., values containing single quotes, semicolons, WIQL keywords).

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Filter expression grammar becomes complex | Medium | Medium | Start with a deliberately minimal grammar (AND/OR, single `:` operator). The grammar is designed for extensibility — new fields and operators can be added without structural changes — but each addition increases maintenance and documentation burden. New operators (`~`, `>`, `<`) should be justified by concrete user demand, not speculative completeness. |
| No-args behavior change surprises users | Low | Low | Show a clear message explaining the change; the old behavior was rarely useful with no arguments. |
| ConsoleAppFramework parameter count | Low | Medium | Adding 3 new named parameters (`--title`, `--description`, `--filter`) brings `TwigCommands.Query()` from 10 to 13 parameters (plus `CancellationToken`). The highest existing count is `TwigCommands.New()` at 9 parameters plus `CancellationToken` and a `params` array (11 total). ConsoleAppFramework's source generator handles each parameter independently — there is no known limit, and all parameters are optional with defaults. Verify in PG-1 that the generated source compiles and binds correctly at 13 parameters before proceeding to PG-2. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `twig query` with no args require a `--help` flag instead of auto-showing? | Low | Proposed: auto-show. Most CLI tools show help for bare invocation. The `--all` flag could be added for users who want to query everything. |
| 2 | Should `--filter` support negation (`NOT state:Done`)? | Low | Deferred to future iteration. Implementing NOT requires additional WIQL `<>` operator handling. |
| 3 | Should the no-args summary render recent query history? | Low | Not in scope — would require tracking past queries in SQLite, a separate feature. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/FilterExpressionParser.cs` | Tokenizer + recursive-descent parser for `--filter` expressions |
| `src/Twig.Domain/ValueObjects/FilterExpression.cs` | AST types: `ComparisonFilter`, `AndFilter`, `OrFilter` (no `FilterOperator` enum — only `:` is supported) |
| `src/Twig/Hints/QueryHintContext.cs` | Sealed record conveying query execution context to `HintEngine`: search flags used, result count, truncation |
| `tests/Twig.Domain.Tests/Services/FilterExpressionParserTests.cs` | Parser unit tests (valid expressions, errors, edge cases) |
| `tests/Twig.Cli.Tests/Commands/QueryCommandNoArgsTests.cs` | Tests for no-args summary rendering |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/QueryParameters.cs` | Add `TitleFilter`, `DescriptionFilter`, `FilterExpression` properties |
| `src/Twig.Domain/Services/WiqlQueryBuilder.cs` | Add `AppendTitleFilter()`, `AppendDescriptionFilter()`, `AppendFilterExpression()` methods |
| `src/Twig/Commands/QueryCommand.cs` | Add no-args detection, title/description/filter params, filter parsing, inline summary rendering |
| `src/Twig/Program.cs` | Add `--title`, `--description`, `--filter` CLI params to `Query()` method |
| `src/Twig/Hints/HintEngine.cs` | Replace static query hints with contextual hints |
| `tests/Twig.Cli.Tests/Commands/QueryCommandTests.cs` | Add tests for title/description filters, filter expression, backward compat |
| `tests/Twig.Domain.Tests/Services/WiqlQueryBuilderTests.cs` | Add tests for new WIQL clause generation |
| `tests/Twig.Domain.Tests/ValueObjects/QueryParametersTests.cs` | Add tests for new properties |
| `tests/Twig.Infrastructure.Tests/Telemetry/TelemetryClientTests.cs` | Add `had_filters`, `showed_summary`, `had_title_filter`, `had_description_filter`, `had_filter_expression` to `SafePropertyKeys` allowlist |

## ADO Work Item Structure

### Epic #1638: Query Command UX Improvements

**Goal**: Deliver four UX improvements to the `twig query` command — no-args help, field-specific search filters, structured query expressions, and discoverability enhancements.

**Issues**: #1639, #1640, #1641, #1642 (all pre-existing — do not create duplicates).

---

### Issue #1639: Show Help/Summary When No Arguments

**Goal**: When `twig query` is invoked with no arguments or filters, show a rich summary of available filters, usage examples, and configured defaults instead of executing a broad query.

**Prerequisites**: None — independent of other Issues.

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1639.1 | Add no-args detection logic to `QueryCommand.ExecuteCoreAsync()` — check all filter params are null/default, short-circuit before WIQL execution | `QueryCommand.cs` | ~40 LoC |
| T-1639.2 | Add `RenderQuerySummary()` private method to `QueryCommand` — outputs human-readable text for all non-suppressed formats (human, json, json-full, json-compact): flag list, configured area paths, usage examples with Spectre.Console markup; minimal returns 0; ids returns 0 with empty output. No `IOutputFormatter` interface change. | `QueryCommand.cs` | ~90 LoC |
| T-1639.3 | Add telemetry for no-args invocation (`had_filters: false`, `showed_summary: true`) and add both keys to `SafePropertyKeys` in `TelemetryClientTests.cs` so the `AllowlistTest_AllCommandCallSites_UseOnlySafeKeys` source-scanning test passes | `QueryCommand.cs`, `TelemetryClientTests.cs` | ~15 LoC |
| T-1639.4 | Write unit tests for no-args detection and summary rendering across all output formats (human, json, json-compact, minimal, ids) | `QueryCommandNoArgsTests.cs` | ~180 LoC |

**Acceptance Criteria**:
- [ ] `twig query` with no arguments shows usage help (exit code 0)
- [ ] `twig query --output json` with no arguments outputs the human-readable summary text (exit code 0)
- [ ] `twig query --output json-compact` with no arguments outputs the human-readable summary text (exit code 0)
- [ ] `twig query --output ids` with no arguments produces empty output (exit code 0)
- [ ] `twig query` with no arguments does NOT call ADO (no network request)
- [ ] `twig query "text"` still executes normally (no regression)
- [ ] `twig query --top 50` alone shows summary (formatting flags don't count as filters)
- [ ] Summary includes all available filter flags with descriptions
- [ ] Summary lists all output formats: human, json, json-full, json-compact, minimal, ids
- [ ] Summary includes default area paths from config (if configured)
- [ ] `had_filters` and `showed_summary` keys added to `SafePropertyKeys` and pass telemetry allowlist test

---

### Issue #1640: Split Search into Title and Description Filters

**Goal**: Add `--title` and `--description` flags that generate independent WIQL `CONTAINS` clauses for field-specific searching, while preserving the existing `searchText` positional argument behavior.

**Prerequisites**: None — independent of other Issues. Can be done in parallel with #1639.

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1640.1 | Add `TitleFilter` and `DescriptionFilter` properties to `QueryParameters` | `QueryParameters.cs` | ~10 LoC |
| T-1640.2 | Add `AppendTitleFilter()` and `AppendDescriptionFilter()` to `WiqlQueryBuilder` — each generates a targeted CONTAINS clause for a single field | `WiqlQueryBuilder.cs` | ~30 LoC |
| T-1640.3 | Add `--title` and `--description` parameters to `QueryCommand.ExecuteAsync()` and `TwigCommands.Query()` in `Program.cs` — wire through to `QueryParameters`, update `BuildQueryDescription()` to describe new filters (flows into `QueryResult.Query` automatically), fix existing `searchText` description from `"title contains"` to `"title or description contains"` (drive-by fix for pre-existing inaccuracy), add `had_title_filter`/`had_description_filter` telemetry booleans, and add both keys to `SafePropertyKeys` in `TelemetryClientTests.cs` | `QueryCommand.cs`, `Program.cs`, `TelemetryClientTests.cs` | ~40 LoC |
| T-1640.4 | Write unit tests: title-only, description-only, both combined, combined with searchText, WIQL builder tests | `QueryCommandTests.cs`, `WiqlQueryBuilderTests.cs` | ~120 LoC |

**Acceptance Criteria**:
- [ ] `twig query --title "API"` generates `[System.Title] CONTAINS 'API'`
- [ ] `twig query --description "implementation"` generates `[System.Description] CONTAINS 'implementation'`
- [ ] `twig query --title "X" --description "Y"` generates both clauses AND-joined
- [ ] `twig query "Z" --title "X"` generates both searchText and title clauses AND-joined
- [ ] Existing `twig query "text"` behavior unchanged (backward-compatible)
- [ ] WIQL escaping applied correctly to title/description values
- [ ] `BuildQueryDescription()` searchText description fixed from "title contains" to "title or description contains"
- [ ] `had_title_filter` and `had_description_filter` keys added to `SafePropertyKeys` and pass telemetry allowlist test

---

### Issue #1641: Add Structured Query Support

**Goal**: Add a `--filter` flag that accepts compound filter expressions and compiles them into WIQL WHERE clauses, enabling complex multi-field conditions in a single query.

**Prerequisites**: #1640 (title/description fields must be in QueryParameters and WiqlQueryBuilder first, since the filter parser references the same field set).

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1641.1 | Define `FilterExpression` AST types (`ComparisonFilter`, `AndFilter`, `OrFilter`) in `Twig.Domain.ValueObjects` — no `FilterOperator` enum needed; only `:` is supported | `FilterExpression.cs` | ~20 LoC |
| T-1641.2 | Implement `FilterExpressionParser` as an **internal static class** (matching `WiqlQueryBuilder`'s `internal static class` pattern — pure transformation, no DI) with tokenizer (lexer) and recursive-descent parser — validates field names against allowlist of 5 fields, handles quoted values and parentheses | `FilterExpressionParser.cs` | ~140 LoC |
| T-1641.3 | Add `FilterExpression` property to `QueryParameters` | `QueryParameters.cs` | ~5 LoC |
| T-1641.4 | Add `AppendFilterExpression()` to `WiqlQueryBuilder` — recursive AST-to-WIQL translation with proper parenthesization and field-to-WIQL-name mapping (5 fields: state/type/assignedTo/areaPath/iterationPath) | `WiqlQueryBuilder.cs` | ~50 LoC |
| T-1641.5 | Add `--filter` parameter to `QueryCommand.ExecuteAsync()` and `TwigCommands.Query()` in `Program.cs` — parse expression, add to `QueryParameters`, handle parse errors, update `BuildQueryDescription()` to describe filter expressions (flows into `QueryResult.Query` automatically), add `had_filter_expression` telemetry boolean, and add the key to `SafePropertyKeys` in `TelemetryClientTests.cs` | `QueryCommand.cs`, `Program.cs`, `TelemetryClientTests.cs` | ~45 LoC |
| T-1641.6 | Write comprehensive parser tests: valid expressions, operator precedence, quoted values, nested parens, error cases, WIQL injection prevention | `FilterExpressionParserTests.cs` | ~160 LoC |
| T-1641.7 | Write integration tests: end-to-end filter expression through QueryCommand | `QueryCommandTests.cs`, `WiqlQueryBuilderTests.cs` | ~60 LoC |

**Acceptance Criteria**:
- [ ] `twig query --filter "state:Doing"` generates `[System.State] = 'Doing'`
- [ ] `twig query --filter "state:Doing AND type:Bug"` generates correct AND-joined WIQL
- [ ] `twig query --filter "(state:Doing OR state:New) AND type:Task"` handles parentheses correctly
- [ ] Invalid field names produce clear error with list of valid fields (`state`, `type`, `assignedTo`, `areaPath`, `iterationPath`)
- [ ] Invalid syntax produces clear error with position indicator
- [ ] `--filter` clauses AND-join with other CLI flags
- [ ] WIQL injection via filter values is prevented (single-quote escaping)
- [ ] `had_filter_expression` key added to `SafePropertyKeys` and passes telemetry allowlist test
- [ ] Error message for unknown fields lists only supported fields and suggests dedicated flags

---

### Issue #1642: Improve Query Discoverability and Documentation

**Goal**: Make the query command's capabilities more discoverable through contextual hints, improved help text, and usage examples that guide users toward powerful query patterns.

**Prerequisites**: #1639 (no-args summary), #1640 (title/description flags) — hints reference these features.

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1642.1 | Define `QueryHintContext` sealed record in `src/Twig/Hints/QueryHintContext.cs` (properties: `bool UsedSearchText`, `int ResultCount`, `bool IsTruncated`). Add optional `QueryHintContext?` parameter to `HintEngine.GetHints()`, wire from `QueryCommand.ExecuteCoreAsync()`, and replace static `case "query":` hints with context-aware selections: no-results, broad-search, truncated, default | `QueryHintContext.cs`, `HintEngine.cs`, `QueryCommand.cs` | ~55 LoC |
| T-1642.2 | Update `TwigCommands.Query()` XML doc comment with usage examples and flag descriptions | `Program.cs` | ~10 LoC |
| T-1642.3 | Write tests for contextual hint logic — verify correct hints for each context (no-results, broad, truncated, default), verify hint suppression for json/minimal formats (existing behavior preserved) | `HintEngineTests.cs`, `QueryCommandTests.cs` | ~80 LoC |

**Acceptance Criteria**:
- [ ] After a broad `searchText` query, hint suggests `--title` or `--description`
- [ ] After truncated results, hint suggests `--top` or additional filters
- [ ] After zero results, hint suggests broadening search
- [ ] Static "twig set" / "twig show" hints only appear when there are results
- [ ] XML doc comment on `Query()` includes at least 2 usage examples
- [ ] Hints are suppressed for json/minimal output (existing behavior preserved)

## PR Groups

### PG-1: No-Args Summary & Title/Description Filters
**Issues**: #1639 (Tasks T-1639.1–T-1639.4) + #1640 (Tasks T-1640.1–T-1640.4)
**Type**: Deep — focused changes in query pipeline with significant logic additions
**Estimated LoC**: ~470
**Files**: ~8
**Predecessors**: None

These two Issues are grouped because they're both independent foundational changes to the query command, and reviewing them together provides context for how the CLI parameter space is evolving. Neither is large enough alone to warrant a separate PR. PG-1 also serves as the validation point for ConsoleAppFramework handling 12+ parameters — if issues arise, they surface here before PG-2 adds the 13th.

### PG-2: Structured Filter Expressions
**Issues**: #1641 (Tasks T-1641.1–T-1641.7)
**Type**: Deep — new parser with AST, error handling
**Estimated LoC**: ~490
**Files**: ~9
**Predecessors**: PG-1 (filter parser depends on title/description field definitions in QueryParameters)

The filter expression parser benefits from focused review. The AST design and error handling need careful scrutiny.

### PG-3: Discoverability & Documentation
**Issues**: #1642 (Tasks T-1642.1–T-1642.3)
**Type**: Wide — touches hints, help text across several files
**Estimated LoC**: ~140
**Files**: ~5
**Predecessors**: PG-1 (hints reference title/description flags), PG-2 (hints reference filter syntax)

Documentation and discoverability polish. Should be reviewed last since it references features from PG-1 and PG-2.

