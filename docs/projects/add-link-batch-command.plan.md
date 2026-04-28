# Add Link Batch Command and MCP Tool

> **Status**: 🔨 In Progress

## Executive Summary

This plan implements `twig link batch` CLI command and `twig_link_batch` MCP tool for bulk
link operations on multiple work items in a single call. Agents performing bulk reorganization
(reparenting many items, attaching multiple artifacts) currently need one `twig link` call per
operation. `link batch` accepts a JSON array of operations — each specifying `parent`,
`unparent`, `reparent`, or `artifact` — dispatches each to the appropriate handler, deduplicates
resync targets for efficiency, and returns aggregate results with per-operation success/failure.
Exit code is 1 if any operation fails. This feature belongs to PR group PG-3
(add-patch-linkbatch-commands).

## Background

### Current Architecture

Twig provides four link CLI commands registered in `Program.cs`:

| Command | Class | Operation |
|---------|-------|-----------|
| `twig link parent <id>` | `LinkCommand.ParentAsync` | Set parent of active item |
| `twig link unparent` | `LinkCommand.UnparentAsync` | Remove parent link |
| `twig link reparent <id>` | `LinkCommand.ReparentAsync` | Change parent |
| `twig link artifact <url>` | `ArtifactLinkCommand.ExecuteAsync` | Add artifact/hyperlink |

MCP equivalents exist in `CreationTools.cs`:

| MCP Tool | Method | Parameters |
|----------|--------|-----------|
| `twig_link` | `CreationTools.Link` | sourceId, targetId, linkType |
| `twig_link_artifact` | `CreationTools.LinkArtifact` | workItemId, url, name |

Each link operation individually:
1. Validates inputs and resolves work items
2. Calls `IAdoWorkItemService.AddLinkAsync` or `RemoveLinkAsync`
3. Resyncs affected items via `SyncCoordinatorFactory.ReadWrite.SyncLinksAsync` (CLI) or
   `SyncCoordinatorFactory.ReadOnly.SyncLinksAsync` (MCP)
4. Returns formatted output

**Key issue**: For N operations, N×2 resync calls occur even when many operations touch the
same items. A batch command can collect all affected IDs and deduplicate resyncs.

### Existing Batch Patterns

The codebase has two batch precedents:

1. **`BatchCommand`** (CLI, `src/Twig/Commands/BatchCommand.cs`): Multi-item state/field/note
   batching. Accepts `--ids`, iterates items, collects `BatchItemResult` list, returns aggregate
   JSON with per-item results. Uses `FormatBatchResultJson()` for JSON output.

2. **`BatchTools`** (MCP, `src/Twig.Mcp/Tools/BatchTools.cs`): Graph-based batch execution
   via `BatchExecutionEngine`. Accepts a JSON graph of sequence/parallel/step nodes. More
   complex than needed for link batch — link batch is operation-array, not graph-based.

### Call-Site Audit

The new `LinkBatchCommand` will call into existing services. No shared services are modified:

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `LinkCommand.cs` | `ParentCoreAsync` etc. | Instance methods on existing command | No impact — LinkBatch will call ADO services directly, not through LinkCommand |
| `ArtifactLinkCommand.cs` | `ExecuteCoreAsync` | Instance method | Same — LinkBatch calls ADO directly |
| `IAdoWorkItemService` | `AddLinkAsync`, `RemoveLinkAsync`, `AddArtifactLinkAsync` | Service interface | Called by LinkBatch, no changes needed |
| `CreationTools.cs` | `Link`, `LinkArtifact` | MCP tool methods | LinkBatch MCP tool added to same file |
| `McpResultBuilder.cs` | Various `Format*` methods | Static result builder | New `FormatLinkBatch` method added |
| `SyncCoordinator` | `SyncLinksAsync` | Cache resync | Called with deduplicated IDs |

## Problem Statement

Agents performing bulk work-item reorganization (e.g., reparenting 10 items under a new parent,
attaching multiple artifact links) must issue individual `twig link` calls. This causes:

1. **N separate resync operations** where many share the same target parent — each CLI call
   resyncs both the child and parent independently, causing redundant ADO fetches.
2. **No aggregate error reporting** — agents must parse individual command outputs to determine
   overall success/failure.
3. **Sequential execution overhead** — each call has full startup, resolution, and resync cost.

## Goals and Non-Goals

### Goals

1. **G-1**: `twig link batch --json '[...]'` accepts a JSON array of link operations and
   executes them sequentially, returning aggregate per-operation results.
2. **G-2**: `twig_link_batch` MCP tool provides equivalent functionality for MCP consumers.
3. **G-3**: Resync targets are deduplicated — if 5 items are reparented under the same parent,
   the parent is resynced once, not 5 times.
4. **G-4**: Exit code 1 if any operation fails; per-operation success/failure in output.
5. **G-5**: Telemetry emits `operation_count` and `succeeded_count` (safe metrics, no PII).

### Non-Goals

1. **NG-1**: Parallel execution of operations — operations execute sequentially (link ordering
   may matter for hierarchy consistency).
2. **NG-2**: Transaction semantics — no rollback on partial failure; completed operations persist.
3. **NG-3**: Graph-based execution — uses flat array, not the `BatchExecutionEngine` graph model.
4. **NG-4**: Support for `related`, `predecessor`, `successor` link types in batch — only
   `parent`, `unparent`, `reparent`, and `artifact` are batch operations.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `twig link batch --json '<json>'` parses a JSON array of operations |
| FR-2 | `twig link batch --stdin` reads JSON from standard input |
| FR-3 | Each operation has `op` (parent/unparent/reparent/artifact), `itemId`, optional `targetId`/`url` |
| FR-4 | Parent: calls `AddLinkAsync(itemId, targetId, HierarchyReverse)` |
| FR-5 | Unparent: resolves current parent, calls `RemoveLinkAsync` |
| FR-6 | Reparent: removes old parent (if any), adds new parent |
| FR-7 | Artifact: calls `AddArtifactLinkAsync(itemId, url, name)` |
| FR-8 | Per-operation result with `itemId`, `op`, `success`, optional `error` |
| FR-9 | Aggregate output: `totalOperations`, `succeeded`, `failed`, `operations[]` |
| FR-10 | Exit code: 0 if all succeed, 1 if any fail |
| FR-11 | `twig_link_batch` MCP tool accepts `operations` JSON string parameter |
| FR-12 | Resync targets deduplicated across all operations |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-safe: no reflection, use `Utf8JsonWriter` for output JSON |
| NFR-2 | Telemetry: `command=link-batch`, `operation_count`, `succeeded_count` |
| NFR-3 | Error isolation: individual operation failures don't abort remaining ops |
| NFR-4 | Max 50 operations per batch (consistent with existing batch limits) |

## Proposed Design

### Architecture Overview

```
CLI: twig link batch --json '[...]'    MCP: twig_link_batch(operations: '...')
         │                                      │
         ▼                                      ▼
    LinkBatchCommand                    CreationTools.LinkBatch
         │                                      │
         ├── Parse JSON array ◄─────────────────┤
         │                                      │
         ▼                                      ▼
    ┌──────────────────────────────────────────────┐
    │  Per-operation dispatch loop                  │
    │  ┌─ parent  → AddLinkAsync(Hierarchy-Rev)    │
    │  ├─ unparent → RemoveLinkAsync(Hierarchy-Rev)│
    │  ├─ reparent → Remove + Add                  │
    │  └─ artifact → AddArtifactLinkAsync          │
    └──────────────────────────────────────────────┘
         │                                      │
         ▼                                      ▼
    Collect resync IDs (HashSet<int>)
         │
         ▼
    Deduplicated SyncLinksAsync per unique ID
         │
         ▼
    Format aggregate results
```

### Key Components

#### 1. `LinkBatchOperation` (sealed record)

Represents a single link operation parsed from JSON input:

```csharp
public sealed record LinkBatchOperation(string Op, int ItemId, int? TargetId, string? Url, string? Name);
```

- `Op`: One of `parent`, `unparent`, `reparent`, `artifact`
- `ItemId`: The work item to operate on
- `TargetId`: Required for `parent` and `reparent`; ignored for `unparent` and `artifact`
- `Url`: Required for `artifact`; ignored for other ops
- `Name`: Optional display name for artifact links

#### 2. `LinkBatchItemResult` (sealed record)

Per-operation result:

```csharp
public sealed record LinkBatchItemResult(int ItemId, string Op, bool Success, string? Error);
```

#### 3. `LinkBatchCommand` (sealed class, CLI)

Primary constructor injection pattern matching existing commands:

```csharp
public sealed class LinkBatchCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorFactory syncCoordinatorFactory,
    OutputFormatterFactory formatterFactory,
    ITelemetryClient? telemetryClient = null,
    TextReader? stdinReader = null,
    TextWriter? stderr = null,
    TextWriter? stdout = null)
```

**Method**: `ExecuteAsync(json, readStdin, outputFormat, ct)`

**Flow**:
1. Parse JSON input (from `--json` or `--stdin`)
2. Validate operation count (≤50)
3. Iterate operations, dispatch each to handler method
4. Collect resync target IDs in `HashSet<int>`
5. Deduplicated resync via `SyncLinksAsync` per unique ID
6. Format and write output
7. Emit telemetry

#### 4. `FormatLinkBatch` (static method on `McpResultBuilder`)

AOT-safe JSON output using `Utf8JsonWriter`:

```json
{
  "totalOperations": 3,
  "succeeded": 2,
  "failed": 1,
  "operations": [
    { "itemId": 42, "op": "parent", "success": true },
    { "itemId": 43, "op": "reparent", "success": true },
    { "itemId": 44, "op": "artifact", "success": false, "error": "URL invalid" }
  ]
}
```

#### 5. `twig_link_batch` (MCP tool in `CreationTools.cs`)

New method on existing `CreationTools` class:

```csharp
[McpServerTool(Name = "twig_link_batch"), Description("...")]
public async Task<CallToolResult> LinkBatch(
    [Description("JSON array of link operations")] string operations,
    [Description("Target workspace")] string? workspace = null,
    CancellationToken ct = default)
```

### Data Flow

1. **Input**: JSON array string → parsed into `List<LinkBatchOperation>`
2. **Validation**: Each operation validated for required fields based on `op` type
3. **Execution**: Sequential dispatch to ADO service methods
4. **Resync collection**: Each operation adds affected IDs to `HashSet<int>`
5. **Deduplicated resync**: Single pass over unique IDs
6. **Output**: Aggregate results formatted as JSON (CLI) or `CallToolResult` (MCP)

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| DD-1: Flat array vs graph | Flat array | Link operations don't need sequencing control; simpler than BatchExecutionEngine graph model |
| DD-2: Direct ADO calls vs delegate to LinkCommand | Direct ADO calls | Avoids active-item dependency; each operation specifies its own itemId |
| DD-3: Sequential execution | Sequential | Parent-child ordering matters; parallel could create race conditions in hierarchy |
| DD-4: Resync strategy | Deduplicated set | Collect all affected IDs, resync each once after all operations complete |
| DD-5: Manual JSON parsing | `Utf8JsonReader` / `JsonDocument` | AOT-safe; no source-gen needed for input since we parse manually |
| DD-6: Error isolation | Continue on failure | Matching `BatchCommand` behavior — individual failures don't abort batch |
| DD-7: Same file for MCP tool | `CreationTools.cs` | Link tools already live here; consistent grouping |

## Dependencies

### External Dependencies
- No new NuGet packages required
- All ADO operations use existing `IAdoWorkItemService` interface

### Internal Dependencies
- `IAdoWorkItemService.AddLinkAsync`, `RemoveLinkAsync`, `AddArtifactLinkAsync`
- `IWorkItemRepository` for resolving items by ID (parent lookup for unparent)
- `SyncCoordinatorFactory` for deduplicated resync
- `ActiveItemResolver` for resolving work items (validating they exist)
- `OutputFormatterFactory` for CLI output formatting
- `McpResultBuilder` for MCP result formatting

### Sequencing Constraints
- None — all dependencies are existing, stable interfaces

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should `unparent` require an explicit `targetId` (the current parent) or auto-resolve it from cache? | Low | Plan: auto-resolve from `item.ParentId` — consistent with `LinkCommand.UnparentAsync`. If the item has no parent, return error for that operation. |
| OQ-2 | Should the 50-operation limit be configurable? | Low | Plan: hardcode at 50, matching `BatchConstants`. Can be made configurable later. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/LinkBatchCommand.cs` | CLI command: JSON parsing, per-op dispatch, deduplicated resync, aggregate output |
| `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | CLI command unit tests |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | MCP tool unit tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Register `link batch` command with `--json`, `--stdin`, `--output` parameters |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `LinkBatchCommand` singleton |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Add `twig_link_batch` MCP tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatLinkBatch` method |

## ADO Work Item Structure

**Parent Issue**: #2378 — Add Link Batch Command and MCP Tool

### Issue #2399: Create LinkBatchCommand.cs

**Goal**: Implement the core `LinkBatchCommand` class with JSON parsing, per-operation
dispatch to ADO services, deduplicated resync, and aggregate output formatting.

**Prerequisites**: None

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2399-1 | Define `LinkBatchOperation` and `LinkBatchItemResult` records | `src/Twig/Commands/LinkBatchCommand.cs` | S |
| T-2399-2 | Implement JSON parsing for operation array (AOT-safe via `JsonDocument`) | `src/Twig/Commands/LinkBatchCommand.cs` | M |
| T-2399-3 | Implement per-operation dispatch: parent, unparent, reparent, artifact handlers | `src/Twig/Commands/LinkBatchCommand.cs` | L |
| T-2399-4 | Implement deduplicated resync collection and execution | `src/Twig/Commands/LinkBatchCommand.cs` | S |
| T-2399-5 | Implement aggregate JSON output formatting (Utf8JsonWriter) | `src/Twig/Commands/LinkBatchCommand.cs` | S |
| T-2399-6 | Implement `--stdin` input path (read from TextReader) | `src/Twig/Commands/LinkBatchCommand.cs` | S |

**Acceptance Criteria**:
- [ ] `LinkBatchCommand.ExecuteAsync` accepts JSON via `--json` or `--stdin`
- [ ] Each operation type dispatches to correct ADO service method
- [ ] Resync targets are collected in `HashSet<int>` and synced once each
- [ ] Returns exit 0 when all operations succeed, exit 1 when any fail
- [ ] JSON output includes per-operation results with itemId, op, success, error

---

### Issue #2400: Register link batch command in Program.cs

**Goal**: Wire `LinkBatchCommand` into the CLI framework with `--json`, `--stdin`, and
`--output` parameters.

**Prerequisites**: #2399

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2400-1 | Add `LinkBatchCommand` singleton registration | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S |
| T-2400-2 | Add `[Command("link batch")]` method in `TwigCommands` | `src/Twig/Program.cs` | S |
| T-2400-3 | Add `"link batch"` to `KnownCommands` array | `src/Twig/Program.cs` | S |
| T-2400-4 | Add `link batch` entry to `GroupedHelp.Show()` help text | `src/Twig/Program.cs` | S |

**Acceptance Criteria**:
- [ ] `twig link batch --json '[...]'` invokes `LinkBatchCommand.ExecuteAsync`
- [ ] `twig link batch --stdin` reads from stdin
- [ ] `-o json` flag controls output format
- [ ] `twig --help` shows `link batch` under Work Items section
- [ ] `twig link batch` is recognized as a known command

---

### Issue #2401: Add twig_link_batch MCP tool to CreationTools.cs

**Goal**: Expose link batch functionality through the MCP server as `twig_link_batch`.

**Prerequisites**: #2399 (uses same dispatch logic pattern), #2402 (result formatting)

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2401-1 | Add `LinkBatch` method to `CreationTools` class with `[McpServerTool]` attribute | `src/Twig.Mcp/Tools/CreationTools.cs` | M |
| T-2401-2 | Implement JSON parsing and validation (reuse pattern from CLI) | `src/Twig.Mcp/Tools/CreationTools.cs` | M |
| T-2401-3 | Implement per-operation dispatch using `WorkspaceContext` services | `src/Twig.Mcp/Tools/CreationTools.cs` | M |
| T-2401-4 | Wire deduplicated resync via `SyncCoordinatorFactory.ReadOnly` | `src/Twig.Mcp/Tools/CreationTools.cs` | S |

**Acceptance Criteria**:
- [ ] `twig_link_batch` MCP tool is discoverable via MCP tool listing
- [ ] Accepts `operations` JSON string and optional `workspace` parameter
- [ ] Returns structured JSON via `McpResultBuilder.FormatLinkBatch`
- [ ] Per-operation dispatch uses workspace-scoped services
- [ ] Resync targets are deduplicated

---

### Issue #2402: Add FormatLinkBatch method to McpResultBuilder.cs

**Goal**: Add AOT-safe result formatting for link batch results.

**Prerequisites**: None (standalone formatting method)

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2402-1 | Define `LinkBatchResult` record in McpResultBuilder.cs or adjacent file | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S |
| T-2402-2 | Implement `FormatLinkBatch` static method using `Utf8JsonWriter` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S |

**Acceptance Criteria**:
- [ ] `FormatLinkBatch` produces JSON with `totalOperations`, `succeeded`, `failed`, `operations[]`
- [ ] Each operation entry has `itemId`, `op`, `success`, optional `error`
- [ ] Uses `Utf8JsonWriter` (AOT-safe, no reflection)
- [ ] Returns `CallToolResult` with `IsError = false` (even on partial failure — caller decides)

---

### Issue #2403: Add telemetry to LinkBatchCommand

**Goal**: Emit telemetry for link batch operations following existing patterns.

**Prerequisites**: #2399

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2403-1 | Add `TelemetryHelper.TrackCommand` call with `command=link-batch` | `src/Twig/Commands/LinkBatchCommand.cs` | S |
| T-2403-2 | Include `operation_count` and `succeeded_count` as `extraMetrics` | `src/Twig/Commands/LinkBatchCommand.cs` | S |

**Acceptance Criteria**:
- [ ] Telemetry event emitted with `command=link-batch`
- [ ] `operation_count` metric reports total operations in batch
- [ ] `succeeded_count` metric reports successful operations
- [ ] No PII in telemetry (no item IDs, titles, URLs)
- [ ] Zero network calls when `TWIG_TELEMETRY_ENDPOINT` is unset

---

### Issue #2404: Create LinkBatchCommandTests.cs

**Goal**: Comprehensive unit tests for the CLI `LinkBatchCommand`.

**Prerequisites**: #2399, #2400

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2404-1 | Test happy path: single parent operation | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | S |
| T-2404-2 | Test all four operation types: parent, unparent, reparent, artifact | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | M |
| T-2404-3 | Test mixed success/failure: exit 1, per-op results | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | M |
| T-2404-4 | Test input validation: empty array, missing fields, invalid op, >50 operations | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | M |
| T-2404-5 | Test `--stdin` input path | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | S |
| T-2404-6 | Test JSON output format matches spec | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | S |
| T-2404-7 | Test resync deduplication (same targetId across multiple ops) | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | M |
| T-2404-8 | Test telemetry emission with correct metrics | `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | S |

**Acceptance Criteria**:
- [ ] All four op types tested independently
- [ ] Mixed success/failure scenario produces exit code 1
- [ ] Invalid JSON produces exit code 2
- [ ] Empty operations array returns success (0 ops, 0 succeeded)
- [ ] Telemetry metrics verified via `ITelemetryClient` mock
- [ ] Resync dedup verified — `SyncLinksAsync` called once per unique ID

---

### Issue #2405: Create CreationToolsLinkBatchTests.cs

**Goal**: MCP-level unit tests for `twig_link_batch`.

**Prerequisites**: #2401, #2402

**Tasks**:

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2405-1 | Test happy path: single parent operation via MCP | `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | S |
| T-2405-2 | Test all four operation types via MCP | `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | M |
| T-2405-3 | Test validation errors (invalid JSON, empty, unknown op) | `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | M |
| T-2405-4 | Test mixed success/failure returns structured result (not IsError) | `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | S |
| T-2405-5 | Test workspace parameter routing | `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | S |

**Acceptance Criteria**:
- [ ] MCP tool returns structured JSON result (not `IsError`) on partial failure
- [ ] Full failure returns `IsError = true` only for validation errors
- [ ] JSON output matches `FormatLinkBatch` format
- [ ] Workspace parameter correctly routes to workspace context
- [ ] Tests extend `CreationToolsTestBase` for consistent mock setup

## PR Groups

### PG-3: add-patch-linkbatch-commands

**Classification**: Deep (few files, complex logic in dispatch and resync dedup)

**Issues included**: #2399, #2400, #2401, #2402, #2403, #2404, #2405

**Estimated LoC**: ~800 (production) + ~600 (tests) = ~1400 total

**Execution order**: Standalone PR group — no predecessor PGs required.

**Files**:
- `src/Twig/Commands/LinkBatchCommand.cs` (new, ~250 lines)
- `src/Twig/Program.cs` (modified, ~15 lines added)
- `src/Twig/DependencyInjection/CommandRegistrationModule.cs` (modified, ~1 line)
- `src/Twig.Mcp/Tools/CreationTools.cs` (modified, ~100 lines added)
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (modified, ~40 lines added)
- `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` (new, ~350 lines)
- `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` (new, ~250 lines)

**Review strategy**: Reviewer should focus on:
1. JSON parsing correctness and AOT safety
2. Resync deduplication logic
3. Error isolation (one op failure doesn't abort others)
4. Telemetry compliance (no PII)

## References

- Existing `LinkCommand.cs`: `src/Twig/Commands/LinkCommand.cs`
- Existing `ArtifactLinkCommand.cs`: `src/Twig/Commands/ArtifactLinkCommand.cs`
- Existing `BatchCommand.cs`: `src/Twig/Commands/BatchCommand.cs` (pattern reference)
- Existing `CreationTools.cs`: `src/Twig.Mcp/Tools/CreationTools.cs`
- Existing `McpResultBuilder.cs`: `src/Twig.Mcp/Services/McpResultBuilder.cs`
- `TelemetryHelper.cs`: `src/Twig/Commands/TelemetryHelper.cs`

## Execution Plan

### PR Group Summary

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | `PG-1-link-batch-command` | #2399, #2400, #2401, #2402, #2403, #2404, #2405 (all tasks) | None (standalone) | Deep |

### Execution Order

**PG-1 — `PG-1-link-batch-command`** is a standalone group with no predecessor PGs. All
issues are implemented together in a single reviewable PR:

1. **Foundation (no deps)**: Issue #2402 (`FormatLinkBatch` in `McpResultBuilder.cs`) and
   Issue #2399 (core `LinkBatchCommand` — records, JSON parsing, dispatch, resync, output)
   can be written in parallel as they are independent of each other.

2. **Wiring (depends on #2399)**: Issue #2400 (CLI registration in `Program.cs` and
   `CommandRegistrationModule.cs`) and Issue #2403 (telemetry in `LinkBatchCommand.cs`)
   complete the CLI side.

3. **MCP tool (depends on #2399 + #2402)**: Issue #2401 adds `twig_link_batch` to
   `CreationTools.cs` and wires `McpResultBuilder.FormatLinkBatch`.

4. **Tests (depends on #2399+#2400 for CLI; #2401+#2402 for MCP)**: Issue #2404
   (`LinkBatchCommandTests.cs`) and Issue #2405 (`CreationToolsLinkBatchTests.cs`)
   exercise the complete implementation.

All ~1,400 LoC across 7 files land in a single PR, keeping the review coherent around a
single feature slice. Size is comfortably within the ≤2,000 LoC / ≤50 files guardrails.

### Validation Strategy

**Self-containment check**: PG-1 introduces only new files and additive changes to existing
files (`Program.cs`, `CommandRegistrationModule.cs`, `CreationTools.cs`,
`McpResultBuilder.cs`). No existing public APIs are modified. The codebase builds and all
existing tests pass before any of this code exists; PG-1 adds its own tests, so `dotnet
test` passes at the PR boundary.

**Per-file validation**:

| File | Validation |
|------|-----------|
| `LinkBatchCommand.cs` | Build: no new compiler warnings. Tests: T-2404-* cover all op types, edge cases, and resync dedup. |
| `Program.cs` + `CommandRegistrationModule.cs` | `twig link batch --help` renders without error; `twig --help` lists `link batch` in Work Items section. |
| `CreationTools.cs` | MCP server starts; `twig_link_batch` appears in tool listing. |
| `McpResultBuilder.cs` | Unit tests for `FormatLinkBatch` verify exact JSON shape. |
| `LinkBatchCommandTests.cs` | All 8 test cases (T-2404-1 through T-2404-8) green. |
| `CreationToolsLinkBatchTests.cs` | All 5 test cases (T-2405-1 through T-2405-5) green. |

**Telemetry compliance**: Verify no PII keys via existing telemetry allowlist tests after
adding `command=link-batch`, `operation_count`, and `succeeded_count`.
