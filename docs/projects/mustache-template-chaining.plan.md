# Output/Input Chaining with Mustache-Style Templating

| Field | Value |
|-------|-------|
| **Work Item** | #2025 |
| **Type** | Issue (under Epic #2023 — Batch MCP Operations) |
| **Status** | To Do |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This design introduces mustache-style template resolution (`{{steps.N.field}}`) for sequential batch steps in the twig MCP server. When batch operations execute sequentially, each step produces a JSON result; subsequent steps can reference fields from prior step outputs using `{{steps.0.id}}`, `{{steps.0.item.title}}`, or other nested property paths. The template engine parses templates at submission time to reject forward references, then resolves values at execution time after each step completes. This enables powerful workflows like "create a work item, then set it as active, then change its state" — all in a single batch call. The implementation is AOT-safe, uses no reflection, and fits cleanly into the existing MCP tool architecture.

## Background

### Current Architecture

The twig MCP server exposes individual tools (e.g., `twig_new`, `twig_set`, `twig_state`, `twig_update`, `twig_note`, `twig_link`) as separate MCP tool calls. Each tool:

1. Receives parameters via the MCP protocol
2. Resolves a `WorkspaceContext` via `WorkspaceResolver`
3. Executes against the ADO API and local SQLite cache
4. Returns a `CallToolResult` containing JSON text (via `McpResultBuilder`)

All JSON output is constructed using `Utf8JsonWriter` (AOT-safe, no reflection). The `McpResultBuilder` class contains ~20 `Format*` methods that write structured JSON with consistent patterns: work item core fields (`id`, `title`, `type`, `state`, `assignedTo`, `isDirty`, `isSeed`, `parentId`), optional workspace keys, and operation-specific data.

### Sibling Issue #2024 — Batch Execution Engine

Issue #2024 ("Batch execution engine: parallel/sequence with safety bounds") is a prerequisite for this work. It will establish:

- **Batch request model**: `BatchRequest` with `sequence` and `parallel` step types
- **Batch execution engine**: Sequential and parallel step execution with safety bounds (max nesting depth, max total operations, timeout)
- **Step result capture**: Each step produces a `StepResult` containing the tool's output JSON
- **MCP tool**: A `twig_batch` tool that accepts a batch request and returns aggregated results

This design (#2025) builds on top of the batch engine by adding template resolution as a layer between step definition and step execution.

### Prior Art in the Codebase

| Component | File | Pattern | Relevance |
|-----------|------|---------|-----------|
| `BranchNameTemplate` | `src\Twig.Domain\ValueObjects\BranchNameTemplate.cs` | Token replacement (`{id}`, `{type}`, `{title}`) via `.Replace()` | Simple token expansion; no nested paths, no cross-step references |
| `BatchCommand` | `src\Twig\Commands\BatchCommand.cs` | CLI batch operations with `BatchItemResult` | Sequential execution with per-item results; no chaining between items |
| `McpResultBuilder` | `src\Twig.Mcp\Services\McpResultBuilder.cs` | `Utf8JsonWriter`-based JSON construction | Defines the output schema that templates must navigate |
| `Result<T>` | `src\Twig.Domain\Common\Result.cs` | Domain result pattern (`IsSuccess`, `Value`, `Error`) | Error propagation pattern to follow |

### Call-Site Audit

This feature introduces new types and a new processing layer — it does not modify existing cross-cutting components. The template engine is a new service injected into the batch executor (from #2024). No existing call sites are modified.

| Component | Impact |
|-----------|--------|
| `McpResultBuilder.BuildJson()` | **Read-only** — templates reference the JSON output structure but don't modify it |
| `WorkspaceResolver.Resolve()` | **No change** — batch tools use the same resolution path |
| `TwigJsonContext` | **Modified** — new batch template types must be registered |
| Batch execution engine (#2024) | **Integration point** — template resolution hooks into step execution pipeline |

## Problem Statement

Today, MCP callers (primarily the conductor SDLC workflow) must issue individual tool calls sequentially, waiting for each result before constructing the next call's parameters. For a common workflow like "create a task, set it active, transition to Doing, add a note":

1. Call `twig_new` → get back `{ "id": 2050, ... }`
2. Parse the JSON response to extract `id`
3. Call `twig_set` with `idOrPattern: "2050"`
4. Call `twig_state` with `stateName: "Doing"`
5. Call `twig_note` with `text: "Started work"`

Each round-trip adds latency and requires the caller to implement JSON parsing and value threading. With batch templating, this becomes a single request:

```json
{
  "type": "sequence",
  "steps": [
    { "tool": "twig_new", "args": { "type": "Task", "title": "My task", "parentId": 1945 } },
    { "tool": "twig_set", "args": { "idOrPattern": "{{steps.0.id}}" } },
    { "tool": "twig_state", "args": { "stateName": "Doing" } },
    { "tool": "twig_note", "args": { "text": "Created via batch: {{steps.0.title}}" } }
  ]
}
```

The gaps this design addresses:

1. **No template syntax** — no way to express references between step outputs and inputs
2. **No template parser** — no component to detect, validate, and extract template expressions
3. **No template resolver** — no component to substitute template expressions with actual values from prior step outputs
4. **No validation** — no way to reject forward references or detect invalid property paths at parse time
5. **No error reporting** — no structured error messages for missing fields, type mismatches, or resolution failures

## Goals and Non-Goals

### Goals

1. **Template syntax**: Define and implement `{{steps.N.field}}` and `{{steps.N.nested.path}}` syntax for referencing prior step outputs
2. **Parse-time validation**: Reject forward references (step N referencing step M where M ≥ N) and parallel sibling references at batch submission time, before any execution begins
3. **Runtime resolution**: Resolve template expressions to concrete values after the referenced step completes, using the step's JSON output
4. **Nested property paths**: Support arbitrarily deep JSON property navigation (e.g., `{{steps.0.item.id}}`, `{{steps.0.fields.System.Title}}`)
5. **Clear error messages**: Produce actionable error messages for: missing fields, non-existent steps, type mismatches (e.g., referencing an object where a string is expected)
6. **AOT compatibility**: All new types are AOT-safe — no reflection, source-generated JSON serialization, sealed classes

### Non-Goals

1. **Arbitrary expressions** — no arithmetic, string concatenation, conditionals, or function calls in templates
2. **Loop/iteration constructs** — no `{{#each}}` or `{{#if}}` blocks
3. **Cross-batch references** — templates only reference steps within the same batch request
4. **Template caching** — templates are parsed per-request; no cross-request cache
5. **Custom formatters** — no type coercion beyond JSON primitive-to-string conversion
6. **Partial template strings in non-string parameters** — template expressions in numeric/boolean parameters must represent the entire value (not embedded in a larger expression)

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | Template expressions use the syntax `{{steps.N.path.to.field}}` where N is a zero-based step index |
| FR-2 | Template expressions can appear in any string-typed parameter of a batch step |
| FR-3 | A single parameter value may contain multiple template expressions mixed with literal text (e.g., `"Created {{steps.0.id}}: {{steps.0.title}}"`) |
| FR-4 | Nested property paths navigate JSON object hierarchies (e.g., `steps.0.item.id` navigates `{ "item": { "id": 42 } }`) |
| FR-5 | Forward references (step N referencing step M where M ≥ N) are rejected at parse time with a clear error |
| FR-6 | Parallel sibling references (steps within the same parallel block referencing each other) are rejected at parse time |
| FR-7 | Missing field references at runtime produce an error that identifies the template expression and the available fields |
| FR-8 | Template resolution converts JSON values to strings: numbers → decimal string, booleans → "true"/"false", null → empty string, strings → literal value |
| FR-9 | Template expressions referencing JSON arrays or objects (non-scalar values) produce a clear error |
| FR-10 | Template-containing parameters in non-string fields (e.g., `parentId`) are coerced: if the entire value is a single template expression, the resolved value is parsed as the target type (int, bool) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All new types are AOT-compatible (no reflection, sealed classes, source-generated JSON) |
| NFR-2 | Template parsing adds < 1ms overhead per step for typical batch sizes (≤50 steps) |
| NFR-3 | Template resolution adds < 1ms overhead per step (JSON path navigation, string substitution) |
| NFR-4 | Memory: template contexts hold step output JSON strings, not parsed DOM trees; JSON is parsed on-demand during resolution |
| NFR-5 | Test coverage: ≥ 90% line coverage for template parsing and resolution logic |

## Proposed Design

### Architecture Overview

The template system is a three-layer pipeline that slots between batch request parsing and step execution:

```
┌─────────────────────────────────────────────────────────────────┐
│                     Batch Request (JSON)                        │
│  { "type": "sequence", "steps": [ ... ] }                       │
└─────────────────┬───────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  1. TEMPLATE PARSER  (TemplateParser)                            │
│     - Scans all step args for {{...}} expressions                │
│     - Extracts TemplateExpression[] per step                     │
│     - Returns ParsedBatchRequest with template metadata          │
└─────────────────┬───────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  2. TEMPLATE VALIDATOR  (TemplateValidator)                      │
│     - Validates step index references (no forward refs)          │
│     - Validates parallel sibling constraints                     │
│     - Returns validation errors or success                       │
└─────────────────┬───────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  3. TEMPLATE RESOLVER  (TemplateResolver)                        │
│     - Called per-step at execution time by batch engine           │
│     - Receives StepOutputStore with completed step results       │
│     - Resolves {{steps.N.path}} → concrete string values         │
│     - Returns resolved parameter dictionary                      │
└─────────────────────────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  BATCH EXECUTOR  (from #2024)                                    │
│     - Executes resolved step against MCP tool                    │
│     - Stores step output in StepOutputStore                      │
│     - Passes control to next step                                │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `TemplateExpression` (Value Object)

Represents a single parsed template reference extracted from a parameter value.

```csharp
namespace Twig.Domain.ValueObjects;

public sealed record TemplateExpression(
    int StepIndex,           // The step being referenced (0-based)
    string[] PropertyPath,   // e.g., ["item", "id"] for steps.0.item.id
    string RawExpression,    // Original text: "steps.0.item.id"
    int StartIndex,          // Position in the source string where {{ begins
    int Length);             // Length of the full {{...}} expression including delimiters
```

#### 2. `TemplateString` (Value Object)

Represents a parameter value that may contain zero or more template expressions mixed with literal text.

```csharp
namespace Twig.Domain.ValueObjects;

public sealed record TemplateString(
    string OriginalValue,                          // Raw string with {{...}} expressions
    IReadOnlyList<TemplateExpression> Expressions, // Extracted expressions (may be empty)
    bool IsFullExpression);                        // True if the entire string is a single {{...}}
                                                   // (enables type coercion for non-string params)
```

#### 3. `TemplateParser` (Domain Service)

Stateless parser that scans string values for `{{steps.N.path}}` expressions.

```csharp
namespace Twig.Domain.Services;

public static class TemplateParser
{
    // Parses a single string value, extracting all template expressions.
    // Returns Result<TemplateString> — fails on malformed syntax.
    public static Result<TemplateString> Parse(string value);

    // Scans all string args in a batch step's parameter dictionary.
    // Returns a dictionary of paramName → TemplateString for params that contain templates.
    public static Result<IReadOnlyDictionary<string, TemplateString>> ParseStepArgs(
        IReadOnlyDictionary<string, object?> args);
}
```

**Parsing rules:**
- Scan for `{{` delimiters; find matching `}}`
- Between delimiters, expect: `steps.` + digit(s) + `.` + dotted property path
- Property path segments: alphanumeric + underscore, split on `.`
- Whitespace inside `{{ }}` is trimmed (e.g., `{{ steps.0.id }}` is valid)
- Unmatched `{{` or `}}` is a parse error
- Empty expression `{{}}` is a parse error

#### 4. `TemplateValidator` (Domain Service)

Validates template expressions against the batch request structure.

```csharp
namespace Twig.Domain.Services;

public static class TemplateValidator
{
    // Validates all template expressions in a parsed batch request.
    // stepIndex: the index of the step containing the templates
    // templates: the parsed template strings for that step's args
    // totalSteps: total number of steps in the sequence
    // parallelSiblingIndices: indices of steps in the same parallel block (if any)
    public static Result ValidateStep(
        int stepIndex,
        IReadOnlyDictionary<string, TemplateString> templates,
        int totalSteps,
        IReadOnlySet<int>? parallelSiblingIndices = null);
}
```

**Validation rules:**
- `StepIndex` must be `< stepIndex` (no forward references)
- `StepIndex` must be `>= 0` (valid index)
- `StepIndex` must not be in `parallelSiblingIndices` (no parallel sibling references)
- `PropertyPath` must have at least one segment

#### 5. `StepOutputStore` (Service)

Thread-safe store for completed step outputs, used during batch execution.

```csharp
namespace Twig.Mcp.Services;

public sealed class StepOutputStore
{
    // Records the JSON output of a completed step.
    public void Record(int stepIndex, string jsonOutput);

    // Checks if a step's output has been recorded.
    public bool HasOutput(int stepIndex);

    // Retrieves the raw JSON output for a step.
    // Returns null if not yet recorded.
    public string? GetOutput(int stepIndex);
}
```

#### 6. `TemplateResolver` (Domain Service)

Resolves template expressions to concrete values using step outputs.

```csharp
namespace Twig.Domain.Services;

public static class TemplateResolver
{
    // Resolves all template expressions in a TemplateString using step outputs.
    // Returns the fully resolved string value, or an error if resolution fails.
    public static Result<string> Resolve(
        TemplateString template,
        Func<int, string?> stepOutputProvider);

    // Resolves a single expression by navigating the JSON output.
    // Uses System.Text.Json for AOT-safe JSON parsing.
    public static Result<string> ResolveExpression(
        TemplateExpression expression,
        string jsonOutput);
}
```

**Resolution algorithm:**
1. Parse the step's JSON output using `JsonDocument.Parse()` (on-demand, not cached)
2. Navigate the `JsonElement` tree using `PropertyPath` segments: `root.GetProperty("item").GetProperty("id")`
3. Convert the final `JsonElement` to string based on `ValueKind`:
   - `String` → `.GetString()`
   - `Number` → `.GetRawText()`
   - `True/False` → `"true"` / `"false"`
   - `Null` → `""` (empty string)
   - `Object` / `Array` → error ("Cannot use object/array as template value")
4. Replace the `{{...}}` expression in the original string with the resolved value
5. For `IsFullExpression == true`, return the raw resolved value (preserving type info for coercion)

### Data Flow

#### Parse-Time Flow (before execution)

```
BatchRequest JSON
  → Parse each step's args for {{...}} expressions
  → For each step with templates:
      → TemplateParser.ParseStepArgs(step.Args)
      → TemplateValidator.ValidateStep(stepIndex, templates, totalSteps, parallelSiblings)
  → If any validation errors: return error immediately (no execution)
  → Attach parsed TemplateString metadata to each step
```

#### Execution-Time Flow (per step)

```
Step N ready to execute
  → Check if step has template-bearing args
  → If yes:
      → For each templated arg:
          → TemplateResolver.Resolve(templateString, stepOutputStore.GetOutput)
          → Replace arg value with resolved string
      → For non-string params with IsFullExpression:
          → Parse resolved string to target type (int.Parse, bool.Parse)
  → Execute step with resolved args
  → stepOutputStore.Record(N, stepResult.JsonOutput)
```

### Design Decisions

#### DD-1: Static methods over injectable services

**Decision:** `TemplateParser`, `TemplateValidator`, and `TemplateResolver` are static classes, not interface-backed services.

**Rationale:** These are pure functions with no external dependencies (no I/O, no ADO calls, no database access). Static methods are simpler, AOT-friendly, and directly testable without DI setup. This follows the existing pattern of `StateResolver.ResolveByName()` and `ConflictResolver.Resolve()`.

#### DD-2: On-demand JSON parsing in resolver

**Decision:** Parse step output JSON on-demand during resolution (using `JsonDocument.Parse()`), rather than pre-parsing and caching DOM trees.

**Rationale:** Step outputs are small (typically < 1KB JSON). Parsing is fast (< 0.1ms). Pre-caching would complicate the `StepOutputStore` lifetime and introduce `JsonDocument` disposal concerns. On-demand parsing also means we only pay the cost for steps that are actually referenced by templates.

#### DD-3: Template expressions in string args only, with type coercion for full-expression non-string params

**Decision:** Templates are detected in string-typed parameters. When a non-string parameter's value is entirely a single template expression (e.g., `"parentId": "{{steps.0.id}}"`), the resolved string is parsed to the target type.

**Rationale:** This matches the MCP protocol where all tool parameters arrive as JSON values. The MCP SDK delivers string parameters as strings, so template expressions naturally appear in string values. For numeric/boolean params, the batch request JSON would need to encode them as strings when they contain templates (e.g., `"parentId": "{{steps.0.id}}"` instead of `"parentId": 42`).

#### DD-4: Flat step indexing with resolved parallel indices

**Decision:** Step indices in templates (`steps.N`) use a flat, pre-order traversal numbering across the entire batch request, including nested parallel/sequence blocks.

**Rationale:** The Epic #2023 description shows flat indexing in its example (`steps.0`, `steps.1`, etc.). Flat indexing is simpler for callers than hierarchical paths (`steps.0.parallel.1.steps.0`). The batch engine (#2024) will assign flat indices during request parsing, making them available for template validation.

#### DD-5: Resolver lives in Domain layer, StepOutputStore in MCP layer

**Decision:** The template parsing, validation, and resolution logic lives in `Twig.Domain` (pure domain logic). The `StepOutputStore` that holds execution state lives in `Twig.Mcp` (runtime/execution state).

**Rationale:** This follows the existing clean architecture boundary: domain services are pure and testable without MCP dependencies. The MCP layer orchestrates execution and manages state. The resolver receives step outputs via a `Func<int, string?>` delegate, decoupling it from the store implementation.

## Alternatives Considered

### Alt-1: Use a third-party template library (Stubble, DotLiquid)

**Pros:** Feature-rich, well-tested, handles edge cases.
**Cons:** AOT incompatibility (most template libraries use reflection for property resolution), dependency on external packages, overkill for property-path-only templating.
**Decision:** Build a minimal custom parser. The template syntax is intentionally constrained to `{{steps.N.path}}` — no conditionals, loops, or helpers. A regex-based parser + `JsonElement` path walker is ~150 lines and fully AOT-safe.

### Alt-2: Use JsonPath expressions instead of dot notation

**Pros:** Standard syntax (`$.steps[0].item.id`), powerful querying.
**Cons:** JsonPath is more complex than needed, introduces array indexing and wildcards that violate the "no arbitrary expressions" constraint, no built-in .NET JsonPath in System.Text.Json (would need a library).
**Decision:** Use simple dot notation. It's sufficient for the use case and simpler to parse/validate.

### Alt-3: Pre-resolve templates by building a dependency graph

**Pros:** Could enable more flexible resolution ordering.
**Cons:** Over-engineered for sequential execution where steps already have a natural order. Adds complexity without benefit since parallel steps cannot reference each other.
**Decision:** Simple sequential resolution: each step resolves templates from the `StepOutputStore` which contains all prior completed steps.

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| `System.Text.Json` | (framework) | AOT-safe JSON parsing for template resolution |
| `ModelContextProtocol` | (existing) | MCP server SDK for tool registration |
| None new | — | No new external packages required |

### Internal Dependencies

| Component | Dependency |
|-----------|------------|
| Issue #2024 | **Hard dependency** — the batch execution engine must exist before templates can be integrated. Specifically: batch request model, step execution pipeline, step result capture. |
| `McpResultBuilder` | **Read-only** — template resolution navigates the JSON output structure defined by these builders |
| `TwigJsonContext` | **Modified** — new batch/template types need `[JsonSerializable]` registration |
| `WorkspaceResolver` | **No change** — batch tool uses the same workspace resolution |

### Sequencing Constraints

1. Issue #2024 must define the batch request model and step execution pipeline
2. This issue (#2025) integrates template resolution into that pipeline
3. The batch MCP tool (from #2024) is the integration point where templates are parsed, validated, and resolved

## Impact Analysis

### Components Affected

| Component | Impact | Details |
|-----------|--------|---------|
| `Twig.Domain` | **New files** | `TemplateExpression`, `TemplateString` (value objects), `TemplateParser`, `TemplateValidator`, `TemplateResolver` (services) |
| `Twig.Mcp` | **New file** | `StepOutputStore` (execution state) |
| `Twig.Mcp` | **Modified** | Batch execution integration point (from #2024) — hook template resolution into step execution |
| `TwigJsonContext` | **Modified** | Register any new serializable types for batch templates |
| `Twig.Domain.Tests` | **New files** | Unit tests for parser, validator, resolver |
| `Twig.Mcp.Tests` | **New files** | Integration tests for `StepOutputStore` and end-to-end template resolution |

### Backward Compatibility

- **Fully backward compatible** — all new functionality. Existing MCP tools and their output formats are unchanged.
- Batch requests without templates work exactly as before (template parser returns empty expression lists).

### Performance Implications

- Template parsing: O(n × m) where n = steps, m = avg params per step. Negligible for bounded batch sizes (≤50 steps).
- Template resolution: One `JsonDocument.Parse()` per referenced step output per resolution. Step outputs are small (<1KB).
- No persistent memory impact — `StepOutputStore` is scoped to a single batch execution and discarded after.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| #2024 batch model changes break template integration | Medium | Medium | Design templates against the Epic #2023 specification (stable); use interfaces/delegates for integration points |
| JSON property paths don't match actual tool output schemas | Low | Medium | Document exact output schemas for each tool in test fixtures; test against real `McpResultBuilder` output |
| Type coercion edge cases (e.g., numeric strings, large numbers) | Low | Low | Use `int.TryParse` / `bool.TryParse` with clear error messages; reject non-parseable values |
| Template syntax conflicts with literal `{{` in user text | Low | Low | Document that literal `{{` must be escaped as `\{\{` if needed; in practice, tool parameters rarely contain mustache-like syntax |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should template expressions support array indexing (e.g., `{{steps.0.items[2].id}}`)? | Low | Current design only supports object property navigation. Array indexing adds complexity. Can be added later if needed. |
| OQ-2 | What is the exact batch request model from #2024? | Low | The Epic #2023 description provides a clear specification. This design uses interfaces/delegates at integration points to accommodate model details. Implementation will adapt to the actual types once #2024 is complete. |
| OQ-3 | Should we support `{{steps.previous.field}}` as an alias for the immediately prior step? | Low | Convenient but not essential. Can be a follow-up enhancement. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src\Twig.Domain\ValueObjects\TemplateExpression.cs` | Value object representing a single parsed `{{steps.N.path}}` reference |
| `src\Twig.Domain\ValueObjects\TemplateString.cs` | Value object representing a parameter value with embedded template expressions |
| `src\Twig.Domain\Services\TemplateParser.cs` | Stateless parser that extracts template expressions from string values |
| `src\Twig.Domain\Services\TemplateValidator.cs` | Validates template expressions against batch structure (no forward refs, no parallel sibling refs) |
| `src\Twig.Domain\Services\TemplateResolver.cs` | Resolves template expressions to concrete values using completed step outputs |
| `src\Twig.Mcp\Services\StepOutputStore.cs` | Thread-safe store for completed step JSON outputs during batch execution |
| `tests\Twig.Domain.Tests\Services\TemplateParserTests.cs` | Unit tests for template parsing |
| `tests\Twig.Domain.Tests\Services\TemplateValidatorTests.cs` | Unit tests for template validation |
| `tests\Twig.Domain.Tests\Services\TemplateResolverTests.cs` | Unit tests for template resolution |
| `tests\Twig.Domain.Tests\ValueObjects\TemplateExpressionTests.cs` | Unit tests for TemplateExpression value object |
| `tests\Twig.Mcp.Tests\Services\StepOutputStoreTests.cs` | Unit tests for StepOutputStore |
| `tests\Twig.Mcp.Tests\Tools\BatchToolsTemplateIntegrationTests.cs` | End-to-end integration tests for template resolution in batch execution |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src\Twig.Infrastructure\Serialization\TwigJsonContext.cs` | Add `[JsonSerializable]` attributes for any new DTOs that need serialization (if batch request model requires it) |

## ADO Work Item Structure

### Issue #2025: Output/input chaining with mustache-style templating

**Goal:** Enable sequential batch steps to reference outputs from prior steps via `{{steps.N.field}}` template syntax, with parse-time validation and runtime resolution.

**Prerequisites:** Issue #2024 (Batch execution engine) must be complete — the batch request model and step execution pipeline must exist.

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1 | Implement `TemplateExpression` and `TemplateString` value objects | `src\Twig.Domain\ValueObjects\TemplateExpression.cs`, `src\Twig.Domain\ValueObjects\TemplateString.cs`, `tests\Twig.Domain.Tests\ValueObjects\TemplateExpressionTests.cs` | Small |
| T-2 | Implement `TemplateParser` — extract `{{steps.N.path}}` expressions from strings | `src\Twig.Domain\Services\TemplateParser.cs`, `tests\Twig.Domain.Tests\Services\TemplateParserTests.cs` | Medium |
| T-3 | Implement `TemplateValidator` — reject forward refs and parallel sibling refs | `src\Twig.Domain\Services\TemplateValidator.cs`, `tests\Twig.Domain.Tests\Services\TemplateValidatorTests.cs` | Small |
| T-4 | Implement `TemplateResolver` — resolve expressions against step output JSON | `src\Twig.Domain\Services\TemplateResolver.cs`, `tests\Twig.Domain.Tests\Services\TemplateResolverTests.cs` | Medium |
| T-5 | Implement `StepOutputStore` and integrate with batch execution pipeline | `src\Twig.Mcp\Services\StepOutputStore.cs`, `tests\Twig.Mcp.Tests\Services\StepOutputStoreTests.cs` | Small |
| T-6 | End-to-end integration: wire template parse → validate → resolve into batch executor, register types in `TwigJsonContext` | `src\Twig.Infrastructure\Serialization\TwigJsonContext.cs`, `tests\Twig.Mcp.Tests\Tools\BatchToolsTemplateIntegrationTests.cs` | Medium |

#### Acceptance Criteria

- [ ] Sequential steps can reference outputs from prior steps using `{{steps.N.field}}` syntax
- [ ] Nested property paths work (e.g., `{{steps.0.item.id}}`)
- [ ] Forward references rejected at parse time with clear error messages
- [ ] Parallel steps cannot reference each other (only prior sequential steps)
- [ ] Missing field references produce clear, actionable error messages
- [ ] Template resolution works for all JSON scalar types (string, number, boolean, null)
- [ ] Non-scalar values (objects, arrays) in template resolution produce clear errors
- [ ] Multiple template expressions in a single parameter value resolve correctly
- [ ] All new types are AOT-compatible (no reflection)
- [ ] ≥ 90% line coverage for template parsing and resolution logic

## PR Groups

### PG-1: Domain Template Engine (Parser + Validator + Resolver)

**Type:** Deep
**Tasks:** T-1, T-2, T-3, T-4
**Files:** ~8 files (4 source + 4 test)
**Estimated LoC:** ~800
**Description:** Pure domain logic — value objects, parser, validator, and resolver. No MCP or infrastructure dependencies. Fully testable in isolation with comprehensive unit tests covering happy paths, edge cases, and error conditions.

**Review focus:**
- Parsing correctness (regex edge cases, whitespace handling, malformed input)
- Validation completeness (forward refs, parallel sibling refs, boundary conditions)
- Resolution correctness (JSON navigation, type conversion, error messages)
- AOT compatibility (sealed classes, no reflection)

### PG-2: MCP Integration (StepOutputStore + Batch Wiring)

**Type:** Deep
**Tasks:** T-5, T-6
**Files:** ~4 files (2 source + 2 test)
**Estimated LoC:** ~400
**Successor:** PG-1
**Description:** Integrates the domain template engine into the MCP batch execution pipeline. Adds `StepOutputStore` for execution state management and wires template resolution into the step execution loop. Includes end-to-end integration tests that exercise the full parse → validate → execute → resolve → execute flow.

**Review focus:**
- Thread safety of `StepOutputStore` (parallel step scenarios)
- Correct integration with batch executor lifecycle
- `TwigJsonContext` registrations (if any)
- End-to-end test coverage with realistic batch scenarios
