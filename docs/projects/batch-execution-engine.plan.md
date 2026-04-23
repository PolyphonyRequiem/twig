# Batch Execution Engine: Parallel/Sequence with Safety Bounds

**Work Item:** #2024
**Type:** Issue (under Epic #2023 — Batch MCP Operations)
**Status:** 📋 Planned
**Plan Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive Summary

This design introduces a `twig_batch` MCP tool that accepts a statically-defined
execution graph of twig MCP tool calls arranged in `sequence` and `parallel` blocks.
The engine validates safety constraints at parse time (max nesting depth: 3, max
operations per batch: 50, no recursive `twig_batch` calls, per-batch timeout), then
executes the graph with appropriate semantics: **fail-fast** for sequences (stop on
first error) and **collect-all** for parallel blocks (run all, aggregate errors).
Sequence blocks support output referencing via `{{steps.N.field}}` placeholders,
which will be resolved at execution time by the sibling Issue #2025 (templating).
This Issue (#2024) focuses on the execution engine, schema validation, and safety
bounds — the templating layer is explicitly out of scope and will integrate via a
clean extension point.

---

## Background

### Current Architecture

The twig MCP server (`twig-mcp`) exposes individual tool methods via the
ModelContextProtocol SDK. Each tool class is annotated with `[McpServerToolType]`
and registered in `Program.cs` via `.WithTools<T>()`. Tool methods return
`CallToolResult` objects built via the `McpResultBuilder` helper.

| Layer | Component | Responsibility |
|-------|-----------|----------------|
| MCP Tools | `ContextTools`, `CreationTools`, `MutationTools`, `NavigationTools`, `ReadTools`, `WorkspaceTools` | Individual MCP tool endpoints |
| Services | `WorkspaceResolver` | Resolves workspace context per tool call |
| Services | `WorkspaceContext` | Bundles all per-workspace services |
| Services | `McpResultBuilder` | JSON response formatting via `Utf8JsonWriter` |
| Services | `McpPendingChangeFlusher` | Headless pending-change flusher |
| Domain | `IAdoWorkItemService` | ADO REST API contract |
| Infrastructure | `AdoRestClient` | HTTP calls to ADO REST API |

### Tool Registration Pattern

Tools are registered explicitly in `Program.cs` (AOT-safe, no reflection):

```csharp
.WithTools<ContextTools>()
.WithTools<ReadTools>()
.WithTools<MutationTools>()
.WithTools<NavigationTools>()
.WithTools<CreationTools>()
.WithTools<WorkspaceTools>()
```

The `twig_batch` tool will follow this same pattern with a new `BatchTools` class.

### Existing Tool Method Signatures

Every MCP tool method follows the same pattern:
1. Validate input parameters
2. Resolve workspace via `resolver.TryResolve(workspace, out ctx, out err)`
3. Perform domain operations via `WorkspaceContext` services
4. Return `CallToolResult` via `McpResultBuilder`

The batch engine needs to **invoke existing tool methods programmatically** — it
cannot call them via MCP protocol (that would be recursive and add unnecessary
round-trips). Instead, it will dispatch directly to the tool class instances.

### Sibling Issue: Templating (#2025)

Issue #2025 "Output/input chaining with mustache-style templating" will implement
the `{{steps.N.field}}` template resolution. This design (#2024) will:
- Define the `BatchStepResult` contract that captures per-step output
- Provide an `ITemplateResolver` extension point that #2025 implements
- Use a no-op resolver by default (templates pass through as literals)

---

## Problem Statement

The conductor SDLC workflow frequently needs to perform multiple twig MCP operations
as a logical unit — e.g., create a work item, set context, change state, add links,
and add notes. Today each operation is a separate MCP tool call with round-trip
overhead between the LLM agent and the MCP server. This creates:

1. **Latency**: Each MCP tool call incurs agent-to-server round-trip overhead. A
   typical "create + set + state + link + note" sequence is 5 round-trips.
2. **Fragility**: If the agent's context window fills or the session times out
   mid-sequence, partially-completed operations leave the workspace in an
   inconsistent state with no rollback.
3. **Cognitive overhead**: The agent must track step outputs manually and thread
   them into subsequent calls (e.g., passing the ID from `twig_new` into `twig_set`).

---

## Goals and Non-Goals

### Goals

1. **G1**: Define a batch request schema supporting `sequence` and `parallel` blocks
   with fixed task counts and nestability.
2. **G2**: Implement an execution engine that dispatches to existing MCP tool methods
   directly (no MCP protocol recursion).
3. **G3**: Enforce safety bounds at parse/validation time before any execution begins:
   max nesting depth (3), max operations per batch (50), no `twig_batch` recursion.
4. **G4**: Apply per-batch execution timeout (configurable, default 120 seconds).
5. **G5**: Provide clear, structured error reporting — per-step results with
   success/failure status, error messages, and execution metadata.
6. **G6**: Define a clean extension point for template resolution (#2025 integration).

### Non-Goals

- **NG1**: Implement template resolution (`{{steps.N.field}}`) — that is #2025.
- **NG2**: Support dynamic task generation, loops, or conditionals within batches.
- **NG3**: Provide rollback/undo semantics for partially-completed batches.
- **NG4**: Support cross-workspace batches (all steps execute in one workspace).
- **NG5**: Add batch-level ADO concurrency throttling (reuses existing per-request
  patterns; batch parallelism is logical, not HTTP-level).

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | Accept a JSON batch request with `sequence` and `parallel` block types |
| FR-2 | Each leaf step specifies a `tool` name and `args` object |
| FR-3 | `sequence` blocks execute steps in order; stop on first failure (fail-fast) |
| FR-4 | `parallel` blocks execute all steps concurrently; collect all results |
| FR-5 | Blocks can nest: sequence containing parallel, parallel containing sequence |
| FR-6 | Validate batch structure before execution: nesting depth ≤ 3, total operations ≤ 50 |
| FR-7 | Reject `twig_batch` as a tool name within a batch (no recursion) |
| FR-8 | Return structured results: per-step status, output, error, duration |
| FR-9 | Support an optional `workspace` parameter at batch level (applies to all steps) |
| FR-10 | Provide `ITemplateResolver` extension point for #2025 integration |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: no reflection-based dispatch; use explicit tool method routing |
| NFR-2 | Per-batch timeout: default 120s, configurable via batch request |
| NFR-3 | Thread-safe: parallel blocks use `Task.WhenAll` with proper isolation |
| NFR-4 | JSON serialization via `Utf8JsonWriter` (consistent with existing tools) |
| NFR-5 | All new types registered in `TwigJsonContext` if they need STJ serialization |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                      twig_batch MCP Tool                     │
│  (BatchTools.cs — [McpServerToolType])                       │
│                                                              │
│  1. Parse JSON → BatchRequest (schema model)                 │
│  2. Validate safety bounds (BatchValidator)                   │
│  3. Execute graph (BatchExecutor)                             │
│  4. Format results (McpResultBuilder extension)               │
└──────────────┬──────────────────────────────────┬────────────┘
               │                                  │
      ┌────────▼────────┐              ┌──────────▼──────────┐
      │ BatchValidator   │              │  BatchExecutor       │
      │                  │              │                      │
      │ • Nesting depth  │              │ • Walks BatchRequest │
      │ • Op count ≤ 50  │              │ • Sequence: serial   │
      │ • No recursion   │              │ • Parallel: WhenAll  │
      │ • Tool name check│              │ • Per-batch timeout  │
      └──────────────────┘              │ • Captures results   │
                                        └──────────┬───────────┘
                                                   │
                                        ┌──────────▼───────────┐
                                        │  ToolDispatcher       │
                                        │                      │
                                        │ Switch on tool name: │
                                        │ • twig_set → ctx.Set │
                                        │ • twig_new → cr.New  │
                                        │ • twig_state → m.St  │
                                        │ • ... (all tools)    │
                                        └──────────────────────┘
```

### Key Components

#### 1. Batch Request Schema (`BatchStep` discriminated union)

The batch request is a tree of steps. Each node is either a **leaf** (tool call) or
a **block** (sequence/parallel container). This maps to a simple discriminated union:

```csharp
// Twig.Mcp/Batch/BatchStep.cs
public abstract record BatchStep
{
    public sealed record ToolCall(
        string Tool,
        Dictionary<string, object?> Args) : BatchStep;

    public sealed record SequenceBlock(
        IReadOnlyList<BatchStep> Steps) : BatchStep;

    public sealed record ParallelBlock(
        IReadOnlyList<BatchStep> Steps) : BatchStep;
}
```

The top-level batch request wraps this with metadata:

```csharp
// Twig.Mcp/Batch/BatchRequest.cs
public sealed record BatchRequest(
    BatchStep Root,
    string? Workspace = null,
    int? TimeoutSeconds = null);
```

The MCP tool receives raw JSON (the `steps` parameter is a JSON string) and
parses it into this model via `BatchRequestParser`. This avoids needing STJ
deserialization attributes on the union type (AOT-safe).

#### 2. BatchRequestParser

Parses the raw JSON string into a `BatchRequest` using `JsonDocument`
(read-only, no reflection needed). The parser:
- Reads `type` field to discriminate `sequence`, `parallel`, or tool call
- Tool calls are identified by presence of `tool` field (no `type` needed)
- Recursively builds the `BatchStep` tree
- Returns `Result<BatchRequest>` with clear error messages for malformed input

```csharp
// Twig.Mcp/Batch/BatchRequestParser.cs
public static class BatchRequestParser
{
    public static Result<BatchRequest> Parse(string json);
}
```

#### 3. BatchValidator

Validates safety constraints on a parsed `BatchRequest` before execution:

```csharp
// Twig.Mcp/Batch/BatchValidator.cs
public static class BatchValidator
{
    public const int MaxNestingDepth = 3;
    public const int MaxOperations = 50;
    private static readonly HashSet<string> ForbiddenTools = new(StringComparer.OrdinalIgnoreCase) { "twig_batch" };

    public static Result Validate(BatchRequest request);
}
```

Validation checks (all at parse time, before execution):
- **Nesting depth**: Walk the tree, tracking depth. Error if > 3.
- **Operation count**: Count all `ToolCall` leaves. Error if > 50.
- **Forbidden tools**: Check all `ToolCall.Tool` names against blocklist. Error if `twig_batch` found.
- **Empty blocks**: Reject empty sequence/parallel blocks.

#### 4. ToolDispatcher

Routes a tool name + args dictionary to the appropriate tool method. This is the
key AOT-compatible piece — it uses a `switch` expression over known tool names
rather than reflection:

```csharp
// Twig.Mcp/Batch/ToolDispatcher.cs
public sealed class ToolDispatcher(
    ContextTools contextTools,
    ReadTools readTools,
    MutationTools mutationTools,
    NavigationTools navigationTools,
    CreationTools creationTools,
    WorkspaceTools workspaceTools)
{
    public async Task<CallToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        string? workspaceOverride,
        CancellationToken ct);
}
```

The dispatcher extracts typed parameters from the args dictionary using
helper methods (`GetString`, `GetInt`, `GetBool`) and calls the corresponding
tool method directly. The `workspaceOverride` from the batch-level `workspace`
parameter is injected into each call (unless the step has its own `workspace` arg).

**Tool routing table** (explicit switch, no reflection):

| Tool Name | Target Method | Required Args |
|-----------|--------------|---------------|
| `twig_set` | `contextTools.Set` | `idOrPattern` |
| `twig_status` | `contextTools.Status` | (none) |
| `twig_tree` | `readTools.Tree` | (optional: `depth`) |
| `twig_workspace` | `readTools.Workspace` | (optional: `all`) |
| `twig_state` | `mutationTools.State` | `stateName`, (optional: `force`) |
| `twig_update` | `mutationTools.Update` | `field`, `value`, (optional: `format`) |
| `twig_note` | `mutationTools.Note` | `text` |
| `twig_discard` | `mutationTools.Discard` | (optional: `id`) |
| `twig_sync` | `mutationTools.Sync` | (none) |
| `twig_new` | `creationTools.New` | `type`, `title`, (optional: `parentId`, `description`, `assignedTo`, `skipDuplicateCheck`) |
| `twig_find_or_create` | `creationTools.FindOrCreate` | `type`, `title`, `parentId`, (optional: `description`, `assignedTo`) |
| `twig_link` | `creationTools.Link` | `sourceId`, `targetId`, `linkType` |
| `twig_show` | `navigationTools.Show` | `id` |
| `twig_query` | `navigationTools.Query` | (many optional filters) |
| `twig_children` | `navigationTools.Children` | `id` |
| `twig_parent` | `navigationTools.Parent` | `id` |
| `twig_sprint` | `navigationTools.Sprint` | (optional: `items`) |
| `twig_list_workspaces` | `workspaceTools.ListWorkspaces` | (none) |

#### 5. BatchExecutor

Walks the `BatchStep` tree and executes it:

```csharp
// Twig.Mcp/Batch/BatchExecutor.cs
public sealed class BatchExecutor(
    ToolDispatcher dispatcher,
    ITemplateResolver templateResolver)
{
    public async Task<BatchResult> ExecuteAsync(
        BatchRequest request,
        CancellationToken ct);
}
```

Execution semantics:
- **SequenceBlock**: Execute steps in order. Each step's `CallToolResult` is
  captured in a `BatchStepResult`. If any step returns `IsError = true`, stop
  immediately — remaining steps get status `Skipped`.
- **ParallelBlock**: Execute all steps concurrently via `Task.WhenAll`. All
  results are collected regardless of individual failures.
- **ToolCall**: Dispatch via `ToolDispatcher`. Before dispatch, run template
  resolution on the args (extension point for #2025; no-op by default).
- **Timeout**: The entire execution is wrapped in a `CancellationTokenSource`
  with the batch timeout. If the timeout fires, in-progress operations are
  cancelled and remaining steps get status `TimedOut`.

#### 6. BatchResult / BatchStepResult

```csharp
// Twig.Mcp/Batch/BatchResult.cs
public sealed record BatchResult(
    IReadOnlyList<BatchStepResult> Results,
    bool Success,
    int TotalSteps,
    int Succeeded,
    int Failed,
    int Skipped,
    long ElapsedMs);

public sealed record BatchStepResult(
    int Index,
    string ToolName,
    BatchStepStatus Status,
    CallToolResult? Result,
    long ElapsedMs,
    string? Error);

public enum BatchStepStatus
{
    Success,
    Failed,
    Skipped,
    TimedOut
}
```

#### 7. ITemplateResolver (extension point for #2025)

```csharp
// Twig.Mcp/Batch/ITemplateResolver.cs
public interface ITemplateResolver
{
    IReadOnlyDictionary<string, object?> Resolve(
        IReadOnlyDictionary<string, object?> args,
        IReadOnlyList<BatchStepResult> priorResults);
}

// Default no-op implementation
public sealed class NoOpTemplateResolver : ITemplateResolver
{
    public static readonly NoOpTemplateResolver Instance = new();

    public IReadOnlyDictionary<string, object?> Resolve(
        IReadOnlyDictionary<string, object?> args,
        IReadOnlyList<BatchStepResult> priorResults) => args;
}
```

### Data Flow

**Batch execution flow:**

```
Client → twig_batch(json) → BatchRequestParser.Parse(json)
                                    │
                                    ▼
                            BatchRequest (tree)
                                    │
                                    ▼
                          BatchValidator.Validate()
                            ┌───────┴───────┐
                           fail           pass
                            │               │
                            ▼               ▼
                      error result    BatchExecutor.ExecuteAsync()
                                            │
                                     ┌──────┴──────┐
                                 sequence       parallel
                                     │              │
                                  serial         WhenAll
                                  dispatch       dispatch
                                     │              │
                                     ▼              ▼
                              ToolDispatcher.DispatchAsync()
                                     │
                                     ▼
                              CallToolResult
                                     │
                                     ▼
                            BatchStepResult[]
                                     │
                                     ▼
                         McpResultBuilder.FormatBatch()
                                     │
                                     ▼
                              CallToolResult (aggregate)
```

### Design Decisions

**DD-1: Direct method dispatch vs. MCP protocol recursion**
- **Decision**: Direct dispatch via `ToolDispatcher` switch expression.
- **Rationale**: MCP protocol calls would add serialization overhead, require
  the MCP client infrastructure, and create the recursive `twig_batch` risk.
  Direct dispatch is faster, simpler, and AOT-safe.

**DD-2: JSON string input vs. structured parameters**
- **Decision**: The batch request is a single JSON string parameter (`request`).
- **Rationale**: MCP tool parameters are flat key-value pairs. The batch request
  is a recursive tree structure that doesn't map well to flat parameters.
  A JSON string is the standard MCP pattern for complex inputs.

**DD-3: Flat result array vs. nested result tree**
- **Decision**: Flat result array with step indices, not a nested tree.
- **Rationale**: The LLM consumer needs to find step results by index
  (e.g., "what was the ID from step 0?"). A flat array with indices is
  simpler to navigate than a mirrored tree structure. For nested blocks,
  steps are numbered sequentially in depth-first order.

**DD-4: Sequence fail-fast vs. collect-all**
- **Decision**: Sequences stop on first error; parallel blocks collect all.
- **Rationale**: Sequence steps often depend on prior steps (e.g., `twig_new`
  → `twig_set`). If step 0 fails, step 1 is meaningless. Parallel steps are
  independent by definition, so all should run.

**DD-5: Args dictionary type**
- **Decision**: `Dictionary<string, object?>` where values are primitives
  (string, int, bool, null) extracted from JSON.
- **Rationale**: MCP args are JSON objects with scalar values. Using `object?`
  with runtime type checks is the simplest AOT-compatible approach. The
  `ToolDispatcher` handles type coercion per tool method.

---

## Dependencies

### External Dependencies

| Dependency | Usage | Already in project? |
|-----------|-------|---------------------|
| `ModelContextProtocol` | MCP SDK for tool registration | ✅ Yes |
| `System.Text.Json` | JSON parsing via `JsonDocument` | ✅ Yes (part of .NET) |

### Internal Dependencies

| Component | Dependency Type |
|-----------|----------------|
| All existing tool classes (`ContextTools`, etc.) | Compile-time — dispatcher calls tool methods |
| `WorkspaceResolver` | Runtime — batch-level workspace resolution |
| `McpResultBuilder` | Compile-time — batch result formatting |
| Issue #2025 (templating) | Runtime extension point — `ITemplateResolver` |

### Sequencing Constraints

- This Issue (#2024) can be implemented independently of #2025 (templating).
- #2025 depends on #2024 for the `BatchStepResult` contract and
  `ITemplateResolver` extension point.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Parallel steps with shared state (e.g., both call `twig_set`) cause race conditions | Medium | Medium | Document that parallel steps should be independent; `twig_set` in a parallel block is a user error. The engine does not enforce this — it's a usage guideline. |
| Large batches (50 ops) exceed ADO rate limits | Low | Medium | Existing per-request retry logic handles 429s. Parallel blocks are logical parallelism (typically 2-5 tasks), not HTTP-level parallelism. |
| ToolDispatcher arg extraction mismatches (wrong type, missing required arg) | Medium | Low | Each tool method already validates its own inputs. Bad args produce tool-level errors that propagate to the batch result. |
| Timeout cancellation leaves operations partially completed | Low | Medium | Documented non-goal (NG3). CancellationToken propagation ensures in-flight HTTP calls are cancelled promptly. |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| OQ-1 | Should parallel blocks within a sequence inherit the step index from the parent scope for template referencing in #2025? (e.g., if a parallel block is step 3 in a sequence, can step 4 reference `{{steps.3.results}}`?) | Low | Deferred to #2025 design |
| OQ-2 | Should we add a `dryRun` mode that validates the batch and reports what would execute without actually executing? | Low | Could be added later as a boolean param |
| OQ-3 | Should the batch result include the raw JSON from each step's `CallToolResult`, or a structured summary? | Low | Raw JSON preserved — the LLM needs full tool output for chaining decisions |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Mcp/Batch/BatchStep.cs` | Discriminated union for batch step types (ToolCall, SequenceBlock, ParallelBlock) |
| `src/Twig.Mcp/Batch/BatchRequest.cs` | Top-level batch request record with metadata |
| `src/Twig.Mcp/Batch/BatchRequestParser.cs` | Parses JSON string into `BatchRequest` tree |
| `src/Twig.Mcp/Batch/BatchValidator.cs` | Validates safety bounds before execution |
| `src/Twig.Mcp/Batch/ToolDispatcher.cs` | Routes tool names to tool method calls (AOT-safe switch) |
| `src/Twig.Mcp/Batch/BatchExecutor.cs` | Walks the step tree, dispatching sequence/parallel |
| `src/Twig.Mcp/Batch/BatchResult.cs` | Result types: `BatchResult`, `BatchStepResult`, `BatchStepStatus` |
| `src/Twig.Mcp/Batch/ITemplateResolver.cs` | Extension point interface + `NoOpTemplateResolver` |
| `src/Twig.Mcp/Tools/BatchTools.cs` | `[McpServerToolType]` with `twig_batch` tool method |
| `tests/Twig.Mcp.Tests/Batch/BatchRequestParserTests.cs` | Parser unit tests |
| `tests/Twig.Mcp.Tests/Batch/BatchValidatorTests.cs` | Validator unit tests |
| `tests/Twig.Mcp.Tests/Batch/ToolDispatcherTests.cs` | Dispatcher routing + arg extraction tests |
| `tests/Twig.Mcp.Tests/Batch/BatchExecutorTests.cs` | Execution engine tests (sequence, parallel, nesting, timeout) |
| `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` | Integration-level tests for the `twig_batch` MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Program.cs` | Add `.WithTools<BatchTools>()` registration |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatBatchResult()` method |

---

## ADO Work Item Structure

### Issue #2024: Batch execution engine: parallel/sequence with safety bounds

**Goal:** Implement the batch execution model for the twig MCP server, enabling
callers to submit composite tool call graphs with safety bounds and structured
error reporting.

**Prerequisites:** None (first Issue under Epic #2023).

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | **Batch schema model**: Define `BatchStep` discriminated union (`ToolCall`, `SequenceBlock`, `ParallelBlock`), `BatchRequest` record, `BatchResult`/`BatchStepResult`/`BatchStepStatus` types, and `ITemplateResolver` interface with `NoOpTemplateResolver` | `src/Twig.Mcp/Batch/BatchStep.cs`, `BatchRequest.cs`, `BatchResult.cs`, `ITemplateResolver.cs` | S |
| T2 | **Batch request parser**: Implement `BatchRequestParser.Parse(string)` using `JsonDocument` to build the `BatchRequest` tree from JSON. Handle all error cases (malformed JSON, unknown types, missing fields). Write comprehensive unit tests. | `src/Twig.Mcp/Batch/BatchRequestParser.cs`, `tests/Twig.Mcp.Tests/Batch/BatchRequestParserTests.cs` | M |
| T3 | **Batch validator**: Implement `BatchValidator.Validate()` with nesting depth, op count, forbidden tool, and empty block checks. Write unit tests for each constraint and edge cases. | `src/Twig.Mcp/Batch/BatchValidator.cs`, `tests/Twig.Mcp.Tests/Batch/BatchValidatorTests.cs` | S |
| T4 | **Tool dispatcher**: Implement `ToolDispatcher` with switch-based routing to all 18 existing tool methods. Implement arg extraction helpers (`GetString`, `GetInt`, `GetBool`, `GetNullableInt`). Write tests for each tool route and arg type coercion. | `src/Twig.Mcp/Batch/ToolDispatcher.cs`, `tests/Twig.Mcp.Tests/Batch/ToolDispatcherTests.cs` | L |
| T5 | **Batch executor**: Implement `BatchExecutor` with sequence (fail-fast), parallel (collect-all), timeout, and nested block execution. Write tests covering all execution modes, error propagation, and timeout behavior. | `src/Twig.Mcp/Batch/BatchExecutor.cs`, `tests/Twig.Mcp.Tests/Batch/BatchExecutorTests.cs` | L |
| T6 | **MCP tool registration + result formatting**: Create `BatchTools.cs` with `twig_batch` method, add `FormatBatchResult()` to `McpResultBuilder`, register in `Program.cs`. Write integration tests. | `src/Twig.Mcp/Tools/BatchTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `src/Twig.Mcp/Program.cs`, `tests/Twig.Mcp.Tests/Tools/BatchToolsTests.cs` | M |

**Acceptance Criteria:**

- [ ] Parallel blocks execute all steps concurrently and collect all results
- [ ] Sequential blocks execute steps in order, stopping on first failure
- [ ] Nesting works (sequence containing parallel, and vice versa, up to 3 levels)
- [ ] Safety bounds enforced at parse time before execution: depth ≤ 3, ops ≤ 50, no `twig_batch` recursion
- [ ] Clear error reporting for bound violations (specific message per constraint)
- [ ] Per-batch timeout cancels in-progress operations
- [ ] `twig_batch` registered and callable via MCP protocol
- [ ] All tests pass with `TreatWarningsAsErrors=true`

---

## PR Groups

### PG-1: Schema, Parser, and Validator (Tasks T1–T3)

**Type:** Deep
**Tasks:** T1, T2, T3
**Estimated LoC:** ~600 (200 model + 200 parser + 200 validator, plus ~400 test)
**Files:** ~7 files
**Description:** Introduces the batch request model, JSON parser, and safety
validator. No execution logic — purely structural. Reviewers can verify the
schema design and constraint enforcement in isolation.
**Successor:** PG-2

### PG-2: Dispatcher, Executor, and MCP Registration (Tasks T4–T6)

**Type:** Wide
**Tasks:** T4, T5, T6
**Estimated LoC:** ~1200 (400 dispatcher + 300 executor + 100 tool + 100 result builder, plus ~600 test)
**Files:** ~8 files
**Description:** Implements the execution engine, tool dispatch routing, and
MCP tool registration. Depends on PG-1 for the schema model. This is the
"make it work" PR — reviewers verify dispatch correctness, execution semantics,
and integration with existing tools.
**Predecessor:** PG-1

