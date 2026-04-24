# Batch MCP Operations

| Field | Value |
|---|---|
| **Epic** | #2023 — Batch MCP Operations |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This design adds batch operation support to the twig MCP server, enabling callers to
submit multiple tool calls as a single `twig_batch` request containing parallel sets
and sequential chains. Sequential steps support output/input chaining via mustache-style
`{{steps.N.field}}` template substitution. The design enforces safety constraints at
parse time — max nesting depth (3), max total operations (50), no recursive batch calls,
per-batch timeout — and follows the existing MCP tool patterns with AOT-safe
`Utf8JsonWriter` response formatting. The batch engine executes parallel blocks with
`Task.WhenAll` (collect-all semantics) and sequential blocks with fail-fast semantics,
returning a structured result that includes per-step outcomes and overall batch status.

---

## Background

### Current MCP Architecture

The twig MCP server exposes 16 tools across 6 tool classes (`ContextTools`,
`ReadTools`, `MutationTools`, `NavigationTools`, `CreationTools`, `WorkspaceTools`).
Each tool is an `[McpServerTool]`-decorated method that:

1. Resolves workspace via `WorkspaceResolver.TryResolve()`
2. Performs domain operations through `WorkspaceContext` services
3. Returns `CallToolResult` built via `McpResultBuilder` using `Utf8JsonWriter`

Tools are registered AOT-safely in `Program.cs` via `.WithTools<T>()`. The MCP SDK
dispatches incoming JSON-RPC `tools/call` messages to these methods, deserializing
parameters from the request and serializing the `CallToolResult` into the response.

### Key Architectural Patterns

| Pattern | Used By | Relevance |
|---|---|---|
| `Utf8JsonWriter` manual JSON | `McpResultBuilder` | Batch results use same pattern |
| `Task.WhenAll` concurrent ops | `SyncCoordinator` | Parallel batch execution |
| Discriminated union results | `ActiveItemResult`, `SyncResult` | Batch step results |
| Aggregate batch results | `SeedPublishBatchResult` | Batch summary shape |
| Per-item error isolation | `SyncItemFailure` | Per-step error capture |
| `IUnitOfWork` transactions | `SeedPublishOrchestrator` | Atomic local operations |
| Optimistic concurrency | `ConflictRetryHelper.PatchWithRetryAsync` | Per-item ADO patches |
| `SyncGuard` protected writes | `ProtectedCacheWriter` | Dirty item protection |

### MCP Tool Dispatch Model

The `ModelContextProtocol` SDK parses incoming `tools/call` requests and invokes the
matching tool method. The tool name comes from `[McpServerTool(Name = "...")]`. The SDK
handles parameter deserialization from JSON into method arguments. The SDK's source-generated
`IMcpServer` interface provides `CallToolAsync(CallToolRequest, ct)` — this is the
internal dispatch entry point we will use for the batch engine to invoke existing tools.

### Call-Site Audit

The `twig_batch` tool is a **new** MCP tool — no existing call sites. However, the batch
engine must invoke existing tools. The relevant call sites are:

| Component | Current Usage | Impact |
|---|---|---|
| `McpResultBuilder.ToResult()` | All tools return via this | Batch wraps individual results |
| `McpResultBuilder.ToError()` | All tools error via this | Batch captures per-step errors |
| `McpResultBuilder.BuildJson()` | Private JSON builder | New `FormatBatch*` methods added |
| `Program.cs` `.WithTools<T>()` | 6 tool classes registered | Add `BatchTools` registration |
| `WorkspaceResolver.TryResolve()` | Called by all tools | Batch may pre-resolve workspace |

---

## Problem Statement

The conductor SDLC workflow frequently needs to perform multiple twig MCP operations
(create items, set states, add links, update fields) as a logical unit. Today, each
operation is a separate MCP `tools/call` round-trip. This causes:

1. **Latency overhead**: N operations require N round-trips through the MCP stdio
   transport, each with JSON-RPC framing overhead.
2. **No atomic sequencing**: Multi-step workflows (create item → set context → update
   description → change state) cannot express dependencies between steps.
3. **No output chaining**: The ID returned from `twig_new` cannot be fed into a
   subsequent `twig_set` without the caller parsing JSON and constructing a new request.
4. **Verbose orchestration**: Callers must implement retry logic, error handling, and
   sequencing for every multi-step workflow.

---

## Goals and Non-Goals

### Goals

1. **Single-request batch execution**: Callers can submit up to 50 tool calls in one
   `twig_batch` MCP request, structured as parallel sets and sequential chains.
2. **Output/input chaining**: Sequential steps can reference outputs from prior steps
   via `{{steps.N.field}}` mustache-style templates, resolved at execution time.
3. **Safety bounds**: Statically validate max nesting depth (3), max operations (50),
   no recursive `twig_batch` calls, and enforce per-batch timeout at parse time.
4. **Composable execution graph**: Sequences can contain parallel blocks and vice versa;
   the full task graph is statically known at submission time.
5. **Structured results**: Return per-step outcomes (success/error with output) plus
   aggregate batch status in a single structured JSON response.

### Non-Goals

1. **Dynamic task generation**: No loops, conditionals, or runtime-determined task
   counts. The graph is fixed at submission time.
2. **Cross-batch state**: No persistent batch state, retry queues, or resumable batches.
   Each batch is a self-contained, fire-once execution.
3. **Arbitrary expressions**: Template substitution is limited to `{{steps.N.field}}`
   path references. No arithmetic, string manipulation, or conditional logic.
4. **Transaction semantics**: Batch operations do not roll back completed steps on
   failure. Sequences are fail-fast but completed steps retain their effects.
5. **Batch-specific ADO APIs**: No new ADO batch endpoints. Individual operations
   continue to use the existing per-item `IAdoWorkItemService` methods.

---

## Requirements

### Functional Requirements

| ID | Requirement |
|---|---|
| FR-1 | `twig_batch` accepts a JSON graph of `sequence` and `parallel` blocks containing tool invocations |
| FR-2 | Parallel blocks execute all steps concurrently via `Task.WhenAll` and collect all results |
| FR-3 | Sequential blocks execute steps in order; a step failure stops the sequence (fail-fast) |
| FR-4 | Sequential steps can reference prior step outputs via `{{steps.N.field}}` templates |
| FR-5 | Template placeholders support nested property paths: `{{steps.0.item.id}}` |
| FR-6 | Forward references (referencing a step that hasn't executed yet) are rejected at parse time |
| FR-7 | Parallel steps cannot reference each other's outputs (detected at parse time) |
| FR-8 | Batch result includes per-step status (success/error/skipped), output, and timing |
| FR-9 | Batch result includes aggregate summary: total, succeeded, failed, skipped counts |
| FR-10 | Missing template field references produce clear error messages at execution time |

### Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-1 | Max nesting depth: 3 levels (validated at parse time) |
| NFR-2 | Max total operations per batch: 50 (validated at parse time) |
| NFR-3 | No recursive `twig_batch` calls within a batch (validated at parse time) |
| NFR-4 | Per-batch timeout: configurable, default 120 seconds |
| NFR-5 | AOT-compatible: no reflection-based JSON, all types in `TwigJsonContext` or use `Utf8JsonWriter` |
| NFR-6 | No new external dependencies beyond existing `ModelContextProtocol` SDK |
| NFR-7 | Batch engine is testable in isolation from MCP transport |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                    MCP Transport (stdio)                  │
├──────────────────────────────────────────────────────────┤
│  BatchTools.Batch()                                      │
│    [McpServerTool(Name = "twig_batch")]                  │
│    ├─ Parse & validate JSON → BatchGraph                 │
│    ├─ Validate safety constraints                        │
│    ├─ Execute via BatchExecutionEngine                   │
│    └─ Format result via McpResultBuilder.FormatBatch()   │
├──────────────────────────────────────────────────────────┤
│  BatchExecutionEngine                                    │
│    ├─ ExecuteAsync(BatchGraph, ct) → BatchResult         │
│    ├─ ExecuteNode (recursive):                           │
│    │   ├─ SequenceNode → serial, fail-fast               │
│    │   ├─ ParallelNode → Task.WhenAll, collect-all       │
│    │   └─ StepNode → resolve templates → invoke tool     │
│    └─ TemplateResolver for {{steps.N.field}}             │
├──────────────────────────────────────────────────────────┤
│  IToolDispatcher                                         │
│    └─ DispatchAsync(toolName, args) → StepResult         │
│       (delegates to IMcpServer.CallToolAsync internally) │
├──────────────────────────────────────────────────────────┤
│  Existing MCP Tools                                      │
│    (ContextTools, MutationTools, CreationTools, etc.)     │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Batch Schema (Input Model)

The `twig_batch` tool accepts a single `graph` parameter as a JSON string. The graph
is a tree of nodes:

```json
{
  "graph": {
    "type": "sequence",
    "steps": [
      {
        "type": "step",
        "tool": "twig_new",
        "args": { "type": "Task", "title": "My Task", "parentId": 42 }
      },
      {
        "type": "step",
        "tool": "twig_set",
        "args": { "idOrPattern": "{{steps.0.id}}" }
      },
      {
        "type": "parallel",
        "steps": [
          {
            "type": "step",
            "tool": "twig_update",
            "args": { "field": "System.Description", "value": "desc", "format": "markdown" }
          },
          {
            "type": "step",
            "tool": "twig_note",
            "args": { "text": "Created via batch" }
          }
        ]
      }
    ]
  },
  "timeoutSeconds": 120
}
```

**Node types:**
- `step`: A single tool invocation with `tool` name and `args` dictionary
- `sequence`: An ordered list of child nodes (steps, parallel blocks, or nested sequences)
- `parallel`: A set of child nodes executed concurrently

Steps are numbered globally in document order (depth-first traversal), starting at 0.
This global index is what `{{steps.N.field}}` references.

#### 2. Domain Model Types

```csharp
// Execution graph nodes — sealed hierarchy
internal abstract record BatchNode;

internal sealed record StepNode(
    int GlobalIndex,
    string ToolName,
    Dictionary<string, object?> Arguments) : BatchNode;

internal sealed record SequenceNode(
    IReadOnlyList<BatchNode> Children) : BatchNode;

internal sealed record ParallelNode(
    IReadOnlyList<BatchNode> Children) : BatchNode;

// Parsed and validated graph
internal sealed record BatchGraph(
    BatchNode Root,
    int TotalStepCount,
    int MaxDepth);

// Per-step execution result
internal sealed record StepResult(
    int StepIndex,
    string ToolName,
    StepStatus Status,
    string? OutputJson,
    string? Error,
    long ElapsedMs);

internal enum StepStatus { Succeeded, Failed, Skipped }

// Aggregate batch result
internal sealed record BatchResult(
    IReadOnlyList<StepResult> Steps,
    long TotalElapsedMs,
    bool TimedOut);
```

#### 3. BatchGraphParser

Responsible for parsing the raw JSON `graph` parameter into a validated `BatchGraph`.
Performs all safety validation at parse time:

- **Structural validation**: Valid JSON, correct node types, required fields present
- **Depth limit**: Max 3 levels of nesting
- **Operation count**: Max 50 total step nodes across the entire graph
- **Recursive batch ban**: No `step` node may have `tool: "twig_batch"`
- **Global index assignment**: Assigns sequential indices to step nodes in
  depth-first traversal order
- **Template validation**: Extracts `{{steps.N.field}}` references from step
  arguments, rejects forward references and intra-parallel references

The parser uses `System.Text.Json` with `JsonDocument` for read-only parsing of the
graph structure — this is AOT-safe because `JsonDocument` doesn't use reflection.

#### 4. BatchExecutionEngine

The engine takes a validated `BatchGraph` and executes it recursively:

```csharp
internal sealed class BatchExecutionEngine(IToolDispatcher dispatcher)
{
    public async Task<BatchResult> ExecuteAsync(
        BatchGraph graph,
        TimeSpan timeout,
        CancellationToken ct);
}
```

**Execution semantics:**

- **SequenceNode**: Execute children left-to-right. If any child fails, skip
  remaining children (they get `StepStatus.Skipped`). Accumulate `StepResult`s.
- **ParallelNode**: Execute all children via `Task.WhenAll`. All children run to
  completion (or failure) regardless of sibling results. Accumulate all results.
- **StepNode**: Resolve templates in arguments → dispatch tool → capture result.
  Wrap the tool's `CallToolResult` into a `StepResult`.

**Template resolution context**: A `StepResult[]` array indexed by global step index.
When executing step N in a sequence, steps 0..N-1 have their outputs available.
Template resolution reads the output JSON from prior steps and extracts the
referenced field using `JsonDocument` property path navigation.

**Timeout**: The engine uses a `CancellationTokenSource.CreateLinkedTokenSource(ct)`
with `CancelAfter(timeout)`. All remaining steps after timeout are marked
`StepStatus.Skipped`.

#### 5. IToolDispatcher

Abstraction over tool invocation, enabling the batch engine to be tested without
the full MCP server:

```csharp
internal interface IToolDispatcher
{
    Task<CallToolResult> DispatchAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken ct);
}
```

**Production implementation** (`McpToolDispatcher`): Uses the MCP SDK's
`IMcpServer.CallToolAsync()` or directly invokes tool methods through a tool
registry. Since the MCP SDK's `WithTools<T>()` already registers tool metadata,
the dispatcher can construct a `CallToolRequest` with the tool name and arguments,
then delegate to the SDK's internal dispatch.

**Test implementation**: Returns canned `CallToolResult` responses for each tool
name, enabling isolated batch engine testing without any workspace infrastructure.

#### 6. TemplateResolver

Handles mustache-style template substitution in step arguments:

```csharp
internal static class TemplateResolver
{
    // At parse time: extract and validate template references
    public static IReadOnlyList<TemplateRef> ExtractRefs(
        Dictionary<string, object?> arguments);

    // At execution time: resolve templates against prior step outputs
    public static Dictionary<string, object?> Resolve(
        Dictionary<string, object?> arguments,
        IReadOnlyList<StepResult> completedSteps);
}

internal sealed record TemplateRef(
    int StepIndex,
    string FieldPath,
    string ArgumentKey,
    string FullPlaceholder);
```

**Template syntax**: `{{steps.N.field.path}}` where:
- `N` is the 0-based global step index
- `field.path` is a dot-delimited JSON property path into the step's output JSON

**Resolution algorithm**:
1. Scan all string values in the arguments dictionary for `{{steps.\d+\..+?}}`
2. For each match, parse the step index and field path
3. Look up `completedSteps[stepIndex].OutputJson`
4. Parse as `JsonDocument`, navigate the property path
5. Replace the placeholder with the resolved value (string coercion for
   non-string JSON types: numbers become their string representation,
   booleans become "true"/"false")

**Full-value replacement**: When an entire argument value is a single template
(e.g., `"{{steps.0.id}}"` with no surrounding text), the resolved value preserves
its original JSON type. For integer fields, this means the resolved argument will
be an integer, not a string. This is critical for parameters like `parentId` and `id`.

#### 7. Batch Result Formatting

New `McpResultBuilder` methods for batch results:

```csharp
public static CallToolResult FormatBatchResult(BatchResult result);
```

Output shape:
```json
{
  "steps": [
    {
      "index": 0,
      "tool": "twig_new",
      "status": "succeeded",
      "output": { /* original tool JSON */ },
      "elapsedMs": 342
    },
    {
      "index": 1,
      "tool": "twig_set",
      "status": "succeeded",
      "output": { /* ... */ },
      "elapsedMs": 15
    }
  ],
  "summary": {
    "total": 4,
    "succeeded": 3,
    "failed": 1,
    "skipped": 0
  },
  "totalElapsedMs": 1205,
  "timedOut": false
}
```

### Data Flow

**Happy path — sequential batch with template chaining:**

```
1. Caller sends tools/call with tool="twig_batch", args={graph: {...}}
2. BatchTools.Batch() receives the graph JSON string
3. BatchGraphParser.Parse(graphJson) → validates → BatchGraph
4. BatchExecutionEngine.ExecuteAsync(graph, timeout, ct)
5.   For each SequenceNode child:
6.     StepNode[0]: TemplateResolver.Resolve(args, []) → no templates
7.                  dispatcher.DispatchAsync("twig_new", args) → CallToolResult
8.                  Extract JSON → StepResult(0, "twig_new", Succeeded, output, ...)
9.     StepNode[1]: TemplateResolver.Resolve(args, [step0Result])
10.                  "{{steps.0.id}}" → parsed from step0 output → "42"
11.                  dispatcher.DispatchAsync("twig_set", {idOrPattern: "42"})
12.                  → StepResult(1, "twig_set", Succeeded, output, ...)
13. BatchResult assembled from all StepResults
14. McpResultBuilder.FormatBatchResult(result) → CallToolResult
15. MCP SDK returns JSON-RPC response to caller
```

**Error path — sequence with mid-step failure:**

```
1-5. Same as above
6.   StepNode[0]: succeeds → StepResult(0, Succeeded)
7.   StepNode[1]: tool returns IsError=true → StepResult(1, Failed, error="...")
8.   StepNode[2]: skipped (fail-fast) → StepResult(2, Skipped)
9. BatchResult: [Succeeded, Failed, Skipped]
10. Response includes all three steps with their statuses
```

### Design Decisions

| Decision | Rationale |
|---|---|
| **Global step indexing (depth-first)** | Simple, unambiguous numbering for template refs. No need for nested path-based addressing (e.g., `steps.0.steps.1`). |
| **`JsonDocument` for template resolution** | AOT-safe read-only JSON navigation without serializer reflection. Already used in test code. |
| **`Utf8JsonWriter` for batch results** | Consistent with all existing MCP result formatting. No need to register batch result types in `TwigJsonContext`. |
| **Separate `IToolDispatcher` interface** | Decouples batch engine from MCP SDK internals. Enables testing with mock dispatchers. Allows future non-MCP batch execution (e.g., CLI). |
| **Parse-time validation over runtime validation** | All structural errors (depth, count, recursive calls, forward refs) caught before any tool executes. No partial execution on invalid graphs. |
| **String-based graph parameter** | The MCP SDK maps tool parameters to method arguments. A single `string graph` parameter containing JSON is simpler than trying to use complex nested types as MCP tool parameters, which would require `TwigJsonContext` registration. |
| **Per-step elapsed timing** | Enables callers to identify slow operations. Cheap to add with `Stopwatch`. |
| **No workspace pre-resolution** | Each tool resolves its own workspace. Batch steps may target different workspaces (rare but supported). |

---

## Alternatives Considered

### Alternative 1: Typed parameter objects vs. JSON string graph

**Considered**: Having the `twig_batch` tool accept typed parameters that map to a
strongly-typed `BatchRequest` class, registered in `TwigJsonContext`.

**Pros**: Compile-time type safety, IDE auto-completion for callers.

**Cons**: MCP tool parameters are flat key-value pairs; nested structures would require
the SDK to deserialize into complex types, which requires `TwigJsonContext` registration.
The graph structure with its recursive node types would need many `[JsonSerializable]`
entries. The MCP SDK passes `Dictionary<string, object?>` for arguments — complex
nesting relies on `JsonElement` anyway.

**Decision**: Use a single `string graph` parameter containing JSON. The
`BatchGraphParser` handles all deserialization using `JsonDocument` (AOT-safe).

### Alternative 2: Direct tool method invocation vs. IToolDispatcher

**Considered**: The batch engine directly instantiates tool classes and calls methods
with resolved parameters.

**Pros**: No abstraction overhead, compile-time method binding.

**Cons**: Tightly couples batch engine to all tool classes. Parameter types differ per
tool (some use `int`, some `string`, some `bool`). Would need reflection or a massive
switch statement to dispatch. Not testable in isolation.

**Decision**: Use `IToolDispatcher` abstraction. The production implementation wraps
the MCP SDK's `IMcpServer` dispatch, which already handles parameter deserialization.

---

## Dependencies

### External Dependencies
- `ModelContextProtocol` SDK (existing) — `IMcpServer.CallToolAsync()` for tool dispatch
- `System.Text.Json` (existing) — `JsonDocument` for graph parsing and template resolution

### Internal Dependencies
- `McpResultBuilder` — extended with batch formatting methods
- `Program.cs` — register `BatchTools` with `.WithTools<BatchTools>()`
- All existing MCP tools — invoked via dispatcher (no modification needed)

### Sequencing Constraints
- Issue #2024 (execution engine) must complete before #2025 (templating)
- The `IToolDispatcher` interface and `BatchGraphParser` are foundations for both issues

---

## Impact Analysis

### Components Affected

| Component | Impact |
|---|---|
| `src/Twig.Mcp/Tools/` | New `BatchTools.cs` file |
| `src/Twig.Mcp/Services/` | New batch engine, parser, template resolver, dispatcher |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatBatchResult()` method |
| `src/Twig.Mcp/Program.cs` | Add `.WithTools<BatchTools>()` registration |
| `tests/Twig.Mcp.Tests/Tools/` | New batch tool tests |
| `tests/Twig.Mcp.Tests/Services/` | New engine, parser, template resolver tests |

### Backward Compatibility
- **Fully backward compatible**: All existing tools are unchanged. The `twig_batch`
  tool is purely additive. No existing MCP clients are affected.

### Performance Implications
- Parallel batch execution reduces total wall-clock time vs. N sequential calls
- Per-batch `CancellationTokenSource` overhead is negligible
- `JsonDocument` parsing for template resolution is allocation-efficient
- No additional ADO API calls beyond what individual tools already make

---

## Security Considerations

- **Recursive batch ban**: Prevents denial-of-service via infinitely recursive
  `twig_batch` calls. Validated at parse time.
- **Operation count limit (50)**: Bounds resource consumption per request.
- **Timeout enforcement**: Prevents runaway batches from consuming server resources.
- **No code execution**: Template substitution is pure data replacement — no
  expression evaluation, no code injection vectors.
- **Workspace isolation preserved**: Each tool resolves its own workspace context;
  batch execution doesn't bypass workspace access controls.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| MCP SDK doesn't expose `IMcpServer` for internal dispatch | Medium | High | Fallback: build a tool registry that directly invokes tool methods via delegate map |
| Template resolution performance on deeply nested JSON | Low | Low | Outputs are small JSON objects; `JsonDocument` is fast |
| Parallel steps contending on shared workspace state | Medium | Medium | Document that parallel steps sharing workspace context may have race conditions; recommend parallel only for independent operations |
| Per-batch timeout may cut off long-running ADO operations mid-flight | Low | Medium | Timeout cancels the `CancellationToken`; individual tool operations handle cancellation gracefully |

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| OQ-1 | Does the `ModelContextProtocol` SDK v1.2 expose a public `McpServerTool.InvokeAsync` or equivalent internal dispatch API? | Low | Investigation of the SDK XML docs shows no public `InvokeAsync` or `CallToolAsync` method on `McpServerTool`. The `McpToolDispatcher` implementation should use direct tool method invocation: inject the tool classes via DI and build a `Dictionary<string, Func<...>>` dispatch map. This is actually more performant (avoids JSON round-trip through SDK) and more testable. |
| OQ-2 | Should parallel steps within a sequence share the same `WorkspaceContext` resolution, or should each step resolve independently? | Low | Current design: each step resolves independently (consistent with existing tool behavior). This means parallel steps in different workspaces work, but parallel steps in the same workspace may have cache contention. |
| OQ-3 | Should batch results embed the full tool output JSON or a summary? | Low | Current design: embed full output (needed for template chaining anyway). For large outputs (tree, workspace), this could produce verbose batch results, but MCP callers typically need the data. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Mcp/Tools/BatchTools.cs` | MCP tool class with `twig_batch` method — entry point for batch operations |
| `src/Twig.Mcp/Services/Batch/BatchGraphParser.cs` | Parses JSON graph into validated `BatchGraph`, enforces safety constraints |
| `src/Twig.Mcp/Services/Batch/BatchExecutionEngine.cs` | Executes `BatchGraph` recursively with sequence/parallel semantics |
| `src/Twig.Mcp/Services/Batch/BatchModels.cs` | Domain model types: `BatchNode`, `StepNode`, `SequenceNode`, `ParallelNode`, `BatchGraph`, `StepResult`, `BatchResult` |
| `src/Twig.Mcp/Services/Batch/IToolDispatcher.cs` | Interface for tool invocation abstraction |
| `src/Twig.Mcp/Services/Batch/McpToolDispatcher.cs` | Production `IToolDispatcher` implementation using MCP SDK dispatch |
| `src/Twig.Mcp/Services/Batch/TemplateResolver.cs` | Mustache-style `{{steps.N.field}}` template extraction and resolution |
| `src/Twig.Mcp/Services/Batch/BatchConstants.cs` | Safety limit constants (max depth, max ops, default timeout) |
| `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` | Integration tests for `twig_batch` tool method |
| `tests/Twig.Mcp.Tests/Services/Batch/BatchGraphParserTests.cs` | Unit tests for graph parsing and validation |
| `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs` | Unit tests for execution engine (sequence, parallel, fail-fast, timeout) |
| `tests/Twig.Mcp.Tests/Services/Batch/TemplateResolverTests.cs` | Unit tests for template extraction, validation, and resolution |
| `tests/Twig.Mcp.Tests/Services/Batch/McpToolDispatcherTests.cs` | Tests for production dispatcher (if SDK dispatch is available) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Program.cs` | Add `.WithTools<BatchTools>()` registration; register `IToolDispatcher` as singleton |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatBatchResult(BatchResult)` method |

### Deleted Files

None.

---

## ADO Work Item Structure

### Epic #2023: Batch MCP Operations

**Goal**: Add batch operation support to the twig MCP server.

---

### Issue #2024: Batch execution engine: parallel/sequence with safety bounds

**Goal**: Implement the core batch execution infrastructure — graph parsing,
validation, execution engine, and tool dispatch — without template resolution.
Template placeholders are treated as literal strings in this issue.

**Prerequisites**: None (foundational issue).

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2024-1 | **Batch domain models and constants**: Define `BatchNode` hierarchy (`StepNode`, `SequenceNode`, `ParallelNode`), `BatchGraph`, `StepResult`, `StepStatus` enum, `BatchResult`, and `BatchConstants` (max depth=3, max ops=50, default timeout=120s). All types `internal sealed`. | `src/Twig.Mcp/Services/Batch/BatchModels.cs`, `src/Twig.Mcp/Services/Batch/BatchConstants.cs` | S |
| T-2024-2 | **BatchGraphParser with safety validation**: Parse JSON string into `BatchGraph` using `JsonDocument`. Validate: required fields, valid node types, depth ≤ 3, total steps ≤ 50, no `twig_batch` in step tool names. Assign global indices via depth-first traversal. Return `Result<BatchGraph>` with clear error messages. | `src/Twig.Mcp/Services/Batch/BatchGraphParser.cs`, `tests/Twig.Mcp.Tests/Services/Batch/BatchGraphParserTests.cs` | M |
| T-2024-3 | **IToolDispatcher interface and mock implementation**: Define `IToolDispatcher` with `DispatchAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken)`. Create `TestToolDispatcher` in test project for engine testing with canned responses. | `src/Twig.Mcp/Services/Batch/IToolDispatcher.cs`, `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs` (test dispatcher inline) | S |
| T-2024-4 | **BatchExecutionEngine**: Implement recursive execution — `SequenceNode` (serial, fail-fast with skip), `ParallelNode` (`Task.WhenAll`, collect-all), `StepNode` (dispatch + capture). Include `Stopwatch` per-step timing and per-batch timeout via `CancellationTokenSource.CreateLinkedTokenSource`. | `src/Twig.Mcp/Services/Batch/BatchExecutionEngine.cs`, `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs` | L |
| T-2024-5 | **McpToolDispatcher (production dispatcher)**: Implement `IToolDispatcher` using MCP SDK's internal tool dispatch. Investigate `IMcpServer` injection or build a delegate-based tool registry mapping tool names to method invocations. Handle parameter deserialization from `Dictionary<string, object?>` to method arguments. | `src/Twig.Mcp/Services/Batch/McpToolDispatcher.cs`, `tests/Twig.Mcp.Tests/Services/Batch/McpToolDispatcherTests.cs` | M |
| T-2024-6 | **BatchTools MCP tool and result formatting**: Create `BatchTools` class with `twig_batch` method. Wire parsing → engine → result formatting. Add `FormatBatchResult()` to `McpResultBuilder`. Register in `Program.cs` with `.WithTools<BatchTools>()` and `IToolDispatcher` as singleton. | `src/Twig.Mcp/Tools/BatchTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `src/Twig.Mcp/Program.cs`, `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` | M |

**Acceptance Criteria**:
- [ ] `twig_batch` accepts a JSON graph with `sequence`, `parallel`, and `step` nodes
- [ ] Parallel blocks execute concurrently and collect all results
- [ ] Sequential blocks execute in order with fail-fast on error
- [ ] Safety constraints validated at parse time: depth ≤ 3, ops ≤ 50, no recursive batch
- [ ] Per-batch timeout aborts remaining steps gracefully
- [ ] Per-step results include status, output, error, and timing
- [ ] Batch result includes aggregate summary (total, succeeded, failed, skipped)
- [ ] All new code has unit tests with ≥ 90% coverage

---

### Issue #2025: Output/input chaining with mustache-style templating

**Goal**: Implement `{{steps.N.field}}` template resolution for sequential batch
steps, enabling output from one step to be used as input to subsequent steps.

**Prerequisites**: Issue #2024 (execution engine must exist).

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2025-1 | **TemplateResolver — extraction and parse-time validation**: Implement `ExtractRefs()` to scan step arguments for `{{steps.N.field.path}}` patterns using regex. Return `IReadOnlyList<TemplateRef>` with step index, field path, argument key, and full placeholder. Validate: no forward refs (step index ≥ current step), no intra-parallel refs. | `src/Twig.Mcp/Services/Batch/TemplateResolver.cs`, `tests/Twig.Mcp.Tests/Services/Batch/TemplateResolverTests.cs` | M |
| T-2025-2 | **TemplateResolver — runtime resolution**: Implement `Resolve()` to substitute templates at execution time. Navigate `JsonDocument` property paths (dot-delimited). Handle type preservation for full-value templates (integer stays integer). Handle missing fields with clear error messages. Handle string interpolation for partial templates (e.g., `"prefix-{{steps.0.id}}-suffix"`). | `src/Twig.Mcp/Services/Batch/TemplateResolver.cs`, `tests/Twig.Mcp.Tests/Services/Batch/TemplateResolverTests.cs` | M |
| T-2025-3 | **Integrate TemplateResolver into BatchGraphParser**: Add template validation to `BatchGraphParser.Parse()` — after building the graph, extract all template refs and validate forward references and parallel references. Reject invalid graphs with descriptive errors. | `src/Twig.Mcp/Services/Batch/BatchGraphParser.cs`, `tests/Twig.Mcp.Tests/Services/Batch/BatchGraphParserTests.cs` | S |
| T-2025-4 | **Integrate TemplateResolver into BatchExecutionEngine**: Before dispatching each `StepNode`, call `TemplateResolver.Resolve()` with the completed steps array. Handle resolution errors as `StepResult` failures. Update engine to pass completed steps context through execution. | `src/Twig.Mcp/Services/Batch/BatchExecutionEngine.cs`, `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs` | M |
| T-2025-5 | **End-to-end template chaining tests**: Integration tests exercising real chaining scenarios: create → set → update, create → link, multi-step with nested paths. Validate type preservation, partial string interpolation, error cases. | `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` | S |

**Acceptance Criteria**:
- [ ] Sequential steps can reference outputs from prior steps via `{{steps.N.field}}`
- [ ] Nested property paths work: `{{steps.0.item.id}}`, `{{steps.1.output.title}}`
- [ ] Forward references rejected at parse time with clear error messages
- [ ] Parallel step cross-references rejected at parse time
- [ ] Missing field references produce clear error at execution time
- [ ] Full-value templates preserve original JSON type (integers, booleans)
- [ ] Partial templates produce string interpolation
- [ ] Template resolution works for all JSON-serializable output fields

---

## PR Groups

### PG-1: Batch Execution Engine (Issue #2024)

**Classification**: Deep (few files, complex execution logic)

**Tasks included**: T-2024-1, T-2024-2, T-2024-3, T-2024-4, T-2024-5, T-2024-6

**Estimated LoC**: ~1,200 (production) + ~800 (tests) = ~2,000 total

**Files** (~12):
- `src/Twig.Mcp/Services/Batch/BatchModels.cs`
- `src/Twig.Mcp/Services/Batch/BatchConstants.cs`
- `src/Twig.Mcp/Services/Batch/BatchGraphParser.cs`
- `src/Twig.Mcp/Services/Batch/IToolDispatcher.cs`
- `src/Twig.Mcp/Services/Batch/McpToolDispatcher.cs`
- `src/Twig.Mcp/Services/Batch/BatchExecutionEngine.cs`
- `src/Twig.Mcp/Tools/BatchTools.cs`
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (modified)
- `src/Twig.Mcp/Program.cs` (modified)
- `tests/Twig.Mcp.Tests/Services/Batch/BatchGraphParserTests.cs`
- `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs`
- `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs`
- `tests/Twig.Mcp.Tests/Services/Batch/McpToolDispatcherTests.cs`

**Successor**: PG-2

---

### PG-2: Template Resolution (Issue #2025)

**Classification**: Deep (few files, intricate template parsing and type preservation logic)

**Tasks included**: T-2025-1, T-2025-2, T-2025-3, T-2025-4, T-2025-5

**Estimated LoC**: ~400 (production) + ~500 (tests) = ~900 total

**Files** (~5):
- `src/Twig.Mcp/Services/Batch/TemplateResolver.cs`
- `src/Twig.Mcp/Services/Batch/BatchGraphParser.cs` (modified — add template validation)
- `src/Twig.Mcp/Services/Batch/BatchExecutionEngine.cs` (modified — integrate templates)
- `tests/Twig.Mcp.Tests/Services/Batch/TemplateResolverTests.cs`
- `tests/Twig.Mcp.Tests/Services/Batch/BatchGraphParserTests.cs` (modified — template validation tests)
- `tests/Twig.Mcp.Tests/Services/Batch/BatchExecutionEngineTests.cs` (modified — template integration tests)
- `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` (modified — e2e template tests)

**Predecessor**: PG-1

---

## References

- [MCP Specification — Tools](https://spec.modelcontextprotocol.io/specification/server/tools/)
- [ModelContextProtocol .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- Existing batch pattern: `SeedPublishOrchestrator` in `src/Twig.Domain/Services/`
- Existing concurrent fetch pattern: `SyncCoordinator.FetchStaleAndSaveAsync`

