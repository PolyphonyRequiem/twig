# Batch State Transitions and Field Updates

> **Status**: ✅ Done
> **Work Item**: [#1782](https://dev.azure.com/) — Batch state transitions and field updates in twig CLI
> **Revision**: 0 (initial draft)

---

## Executive Summary

Today, applying a state transition and field updates to a work item requires multiple
sequential CLI invocations (`twig state`, `twig update`, `twig note`), each performing
its own fetch → conflict-resolve → PATCH → resync cycle — producing 3+ HTTP round-trips
per operation. This plan introduces a `twig batch` command that composes state transitions,
field updates, and notes into a single CLI invocation with one fetch, one conflict check,
one PATCH, and one cache resync. The feature also supports multi-item targeting
(comma-separated IDs) for bulk triage scenarios. A corresponding `twig_batch` MCP tool
enables agents to perform atomic multi-operation mutations in a single tool call.

## Background

### Current Architecture

State transitions and field updates follow a **push-on-write** pattern: CLI commands
(`StateCommand`, `UpdateCommand`) immediately push changes to ADO via the REST API
rather than staging locally. Each command independently performs:

1. **Resolve** active item from `IContextStore` → `IWorkItemRepository`
2. **Fetch** remote version via `IAdoWorkItemService.FetchAsync()`
3. **Conflict-resolve** via `ConflictResolutionFlow.ResolveAsync()`
4. **PATCH** via `ConflictRetryHelper.PatchWithRetryAsync()` — single `FieldChange[]`
5. **Auto-push notes** via `AutoPushNotesHelper.PushAndClearAsync()`
6. **Resync cache** — re-fetch from ADO + save to `IWorkItemRepository`

The ADO REST API's `PATCH /_apis/wit/workitems/{id}` endpoint already accepts an array
of JSON Patch operations, meaning multiple field changes (including `System.State`) can
be applied in a single HTTP request. The existing `PatchAsync(int id,
IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct)` method
on `IAdoWorkItemService` already builds and sends this array — the bottleneck is that
CLI commands only ever send a single `FieldChange` per call.

### Prior Art in the Codebase

| Component | Batch Pattern | Relevance |
|-----------|--------------|-----------|
| `EditCommand` | Parses multiple fields from editor output, patches all in one `PatchAsync` call | **Direct precedent** — proves multi-field PATCH works |
| `PendingChangeFlusher` | Iterates dirty items, per-item fetch+patch+resync with continue-on-failure (FR-7) | Multi-item iteration pattern |
| `FetchBatchAsync()` | Chunks ≤200 IDs per ADO REST call for bulk reads | Batch read pattern |
| `AddChangesBatchAsync()` | Atomic multi-change SQLite insert within single transaction | Batch write pattern |
| `show-batch` command | Comma-separated IDs, cache-only lookup | CLI batch ID parsing pattern |

### Call-Site Audit

The batch command introduces a new code path rather than modifying existing ones.
However, it reuses the following shared components:

| Component | File | Current Usage | Impact |
|-----------|------|---------------|--------|
| `ConflictRetryHelper.PatchWithRetryAsync` | `Infrastructure/Ado/ConflictRetryHelper.cs` | `StateCommand`, `UpdateCommand`, `EditCommand`, `PendingChangeFlusher` | **No change** — called with larger `FieldChange[]` |
| `ConflictResolutionFlow.ResolveAsync` | `Commands/ConflictResolutionFlow.cs` | `StateCommand`, `UpdateCommand`, `PendingChangeFlusher` | **No change** — called once per item |
| `AutoPushNotesHelper.PushAndClearAsync` | `Infrastructure/Ado/AutoPushNotesHelper.cs` | `StateCommand`, `UpdateCommand`, `EditCommand` | **No change** — called once after PATCH |
| `StateTransitionService.Evaluate` | `Domain/Services/StateTransitionService.cs` | `StateCommand`, `FlowTransitionService`, MCP `twig_state` | **No change** — called for validation |
| `StateResolver.ResolveByName` | `Domain/ValueObjects/StateResolver.cs` | `StateCommand`, MCP `twig_state` | **No change** — called for partial name resolution |
| `ActiveItemResolver` | `Domain/Services/ActiveItemResolver.cs` | All mutation commands | **No change** — used for item resolution |
| `OutputFormatterFactory` | `Formatters/OutputFormatterFactory.cs` | All commands | **No change** — used for output |
| `McpResultBuilder` | `Mcp/Services/McpResultBuilder.cs` | MCP mutation tools | **Extended** — new `FormatBatch` method |

## Problem Statement

Copilot agents and power users performing work-item lifecycle operations must execute
multiple sequential CLI commands to combine a state transition with field updates. A
typical "start work" workflow requires three separate commands:

```bash
twig state Active                                              # 3 HTTP calls
twig update System.AssignedTo "Daniel Green"                   # 3 HTTP calls
twig note "Starting: implement batch command"                  # 1 HTTP call
```

This produces **7 HTTP round-trips** and **3 process invocations** for what is
semantically a single atomic operation. The overhead compounds for MCP agents that
orchestrate dozens of work items during SDLC workflows — each `twig_state` +
`twig_update` pair costs ~2 seconds of network latency.

Bulk triage scenarios (closing multiple completed tasks) require a shell loop:
```bash
for id in 1234 1235 1236; do twig set $id && twig state Done; done
```

This is error-prone (partial failures leave inconsistent state) and slow (6N HTTP
calls for N items).

## Goals and Non-Goals

### Goals

1. **G-1**: Combine state transition + field updates + note into a single CLI invocation
   (`twig batch`) with one fetch, one PATCH, one resync per item.
2. **G-2**: Reduce HTTP round-trips from 3N to N+2 for combined operations on a single item
   (1 fetch + 1 patch + 1 resync = 3, vs 9 for three separate commands).
3. **G-3**: Support multi-item targeting via comma-separated IDs for bulk triage.
4. **G-4**: Provide an MCP `twig_batch` tool for agent use with the same semantics.
5. **G-5**: Continue-on-failure for multi-item batches (consistent with `PendingChangeFlusher` FR-7).
6. **G-6**: JSON output format for machine-readable batch results.

### Non-Goals

- **NG-1**: Cross-item transactions (all-or-nothing for multiple items). Each item is
  patched independently; partial success is acceptable and reported.
- **NG-2**: Batch operations on seed items. Seeds use local staging, not push-on-write.
  The `batch` command targets published ADO items only.
- **NG-3**: Interactive conflict resolution for multi-item batches. Multi-item mode
  auto-accepts remote on conflict (consistent with MCP behavior). Single-item mode
  preserves the existing interactive conflict resolution flow.
- **NG-4**: WIQL-based targeting (e.g., "batch all items matching query"). Explicit IDs
  only. Users can compose with `twig query` + shell piping.
- **NG-5**: Batch operations that span multiple ADO projects or organizations.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `twig batch --state <name>` applies a state transition to the active item |
| FR-2 | `twig batch --set <field>=<value>` applies a field update; repeatable for multiple fields |
| FR-3 | `twig batch --note <text>` adds a comment after the PATCH succeeds |
| FR-4 | `--state`, `--set`, and `--note` are independently optional but at least one must be specified |
| FR-5 | `--id <id>` targets a specific item instead of the active item |
| FR-6 | `--ids <id1,id2,...>` targets multiple items; each item is processed independently |
| FR-7 | Multi-item batches continue past individual failures and report per-item results (FR-7 parity) |
| FR-8 | State transitions are validated via `StateTransitionService.Evaluate()` before patching |
| FR-9 | `--set` values support `--format markdown` for Markdown→HTML conversion |
| FR-10 | JSON output (`-o json`) produces structured `BatchResult` with per-item success/failure |
| FR-11 | MCP `twig_batch` tool accepts `state`, `fields` (array of key-value), `note`, and optional `ids` |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: no reflection, all serialization via `TwigJsonContext` |
| NFR-2 | Single PATCH per item regardless of operation count (state + N field updates = 1 HTTP PATCH) |
| NFR-3 | Telemetry: emit `batch` command event with safe properties only (operation count, item count, duration) |
| NFR-4 | Single-item mode latency ≤ existing `state` + `update` sequential latency (regression guard) |

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│  CLI Layer (src/Twig)                               │
│                                                     │
│  Program.cs                                         │
│    └─ twig batch --state X --set F=V --note T       │
│         │                                           │
│  BatchCommand.cs                                    │
│    ├─ Parse --set key=value pairs                   │
│    ├─ Validate: ≥1 operation specified              │
│    ├─ Resolve target item(s)                        │
│    ├─ For each item:                                │
│    │   ├─ FetchAsync (remote)                       │
│    │   ├─ ConflictResolutionFlow.ResolveAsync        │
│    │   ├─ StateTransitionService.Evaluate (if state)│
│    │   ├─ Build FieldChange[] (state + fields)      │
│    │   ├─ ConflictRetryHelper.PatchWithRetryAsync   │
│    │   ├─ AddCommentAsync (if note)                 │
│    │   ├─ AutoPushNotesHelper (residual notes)      │
│    │   └─ Resync cache (fetch + save)               │
│    └─ Return BatchResult                            │
│                                                     │
├─────────────────────────────────────────────────────┤
│  MCP Layer (src/Twig.Mcp)                           │
│                                                     │
│  MutationTools.cs                                   │
│    └─ twig_batch(state?, fields?, note?, ids?)      │
│         └─ Same flow as BatchCommand, headless      │
│            (auto-accept remote on conflict)          │
│                                                     │
├─────────────────────────────────────────────────────┤
│  Domain Layer (src/Twig.Domain) — NO CHANGES        │
│  Infrastructure Layer — NO CHANGES                  │
│    (Existing services are reused directly)           │
└─────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `BatchCommand` (CLI Layer)

**Responsibility**: Parse CLI arguments, orchestrate fetch-validate-patch-resync cycle
for one or more items.

**Constructor Dependencies** (primary constructor pattern):
- `ActiveItemResolver` — item resolution
- `IWorkItemRepository` — cache read/write
- `IAdoWorkItemService` — ADO REST operations
- `IPendingChangeStore` — for auto-push notes
- `IProcessConfigurationProvider` — state transition validation
- `IConsoleInput` — interactive conflict resolution (single-item mode)
- `OutputFormatterFactory` — output formatting
- `HintEngine` — post-operation hints
- `IPromptStateWriter?` — prompt state update

**Method Signature**:
```csharp
public async Task<int> ExecuteAsync(
    string? state = null,
    string[]? set = null,
    string? note = null,
    int? id = null,
    string? ids = null,
    string outputFormat = OutputFormatterFactory.DefaultFormat,
    string? format = null,
    CancellationToken ct = default)
```

**Per-Item Processing Flow**:
1. Fetch remote via `adoService.FetchAsync(itemId)`
2. Run `ConflictResolutionFlow.ResolveAsync()` (interactive for single-item, skip for multi)
3. If `--state` specified:
   a. Resolve state name via `StateResolver.ResolveByName()`
   b. Validate via `StateTransitionService.Evaluate()`
   c. Prompt for confirmation on backward/cut transitions (single-item only)
   d. Add `FieldChange("System.State", currentState, newState)` to change list
4. For each `--set key=value`:
   a. Parse key/value from `=` delimiter
   b. Apply `--format markdown` conversion if specified
   c. Add `FieldChange(key, null, value)` to change list
5. PATCH all changes in single call via `ConflictRetryHelper.PatchWithRetryAsync()`
6. If `--note` specified, push comment via `adoService.AddCommentAsync()`
7. Auto-push residual notes via `AutoPushNotesHelper.PushAndClearAsync()`
8. Resync cache: fetch + save
9. Return per-item result

#### 2. `BatchResult` Value Object

```csharp
public sealed record BatchResult(
    IReadOnlyList<BatchItemResult> Items,
    int TotalFieldChanges,
    int TotalNotes);

public sealed record BatchItemResult(
    int ItemId,
    string Title,
    bool Success,
    string? Error,
    string? PreviousState,
    string? NewState,
    int FieldChangeCount);
```

These are lightweight CLI-layer records (not domain entities). They support both
human-readable and JSON output rendering.

#### 3. MCP `twig_batch` Tool

Added to `MutationTools.cs` with the same orchestration logic but:
- No interactive conflict resolution (auto-accept remote, consistent with existing MCP tools)
- No confirmation prompts (backward/cut transitions require `force: true`)
- Returns `CallToolResult` via `McpResultBuilder.FormatBatchResult()`

### Data Flow

**Single-item batch (`twig batch --state Done --set System.AssignedTo="Alice" --note "Complete"`):**

```
CLI parse → BatchCommand.ExecuteAsync()
  │
  ├─ ActiveItemResolver.GetActiveItemAsync() → WorkItem (local cache)
  │
  ├─ adoService.FetchAsync(id) → WorkItem (remote)         ← 1 HTTP GET
  │
  ├─ ConflictResolutionFlow.ResolveAsync(local, remote)
  │
  ├─ StateTransitionService.Evaluate(config, type, "Active", "Done")
  │     → TransitionResult { Kind=Forward, IsAllowed=true }
  │
  ├─ Build changes: [
  │     FieldChange("System.State", "Active", "Done"),
  │     FieldChange("System.AssignedTo", null, "Alice")
  │   ]
  │
  ├─ ConflictRetryHelper.PatchWithRetryAsync(                ← 1 HTTP PATCH
  │     adoService, id, changes, remote.Revision)
  │
  ├─ adoService.AddCommentAsync(id, "Complete")              ← 1 HTTP POST
  │
  ├─ AutoPushNotesHelper.PushAndClearAsync(id, ...)          ← 0-1 HTTP POST
  │
  └─ adoService.FetchAsync(id) → save to cache              ← 1 HTTP GET
                                                    Total: 4 HTTP calls
```

Compare with sequential commands: `twig state Done` (3 calls) + `twig update ...` (3 calls)
+ `twig note ...` (1 call) = **7 HTTP calls**.

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | CLI-layer command, no new domain service | The existing domain building blocks (StateTransitionService, FieldChange, ConflictRetryHelper) are sufficient. Adding a domain service would be over-engineering for composition logic. |
| DD-2 | `--set` uses `key=value` format | Avoids ambiguity with positional args. Consistent with common CLI tools (`docker run --env KEY=VALUE`). The `=` delimiter is unambiguous for field ref names which never contain `=`. |
| DD-3 | Multi-item mode skips interactive conflict resolution | Interactive prompts for N items would be impractical. Auto-accept-remote is consistent with MCP behavior and PendingChangeFlusher semantics. |
| DD-4 | Notes are pushed AFTER the PATCH, not included in it | ADO comments are a separate API (`/comments`), not a field. This is consistent with existing `StateCommand` and `UpdateCommand` behavior. |
| DD-5 | State validation happens before PATCH (fail-fast) | If the state transition is invalid, we reject immediately without making any HTTP calls. Field updates in the same batch are also skipped. |
| DD-6 | `BatchResult` is a CLI-layer record, not a domain type | It exists only for output formatting. Adding it to the domain would create an unnecessary dependency from domain → CLI concerns. |
| DD-7 | `--format markdown` applies to ALL `--set` values | Simpler than per-field format specifiers. Users who need mixed formats can use separate `twig update` calls for non-markdown fields. |

## Dependencies

### External Dependencies
- None. All required ADO REST API capabilities (`PATCH` with multiple operations) are
  already exercised by the existing `EditCommand`.

### Internal Dependencies
- `ConflictRetryHelper`, `ConflictResolutionFlow`, `AutoPushNotesHelper` — reused as-is
- `StateTransitionService`, `StateResolver` — reused as-is
- `ActiveItemResolver` — reused as-is
- `OutputFormatterFactory`, `HintEngine` — reused as-is

### Sequencing Constraints
- None. This is a new feature with no blockers.

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `Program.cs` | New command registration (3 lines) | Low — additive |
| `CommandRegistrationModule.cs` | New DI registration (1 line) | Low — additive |
| `MutationTools.cs` | New MCP tool method (~60 lines) | Low — additive, follows existing patterns |
| `McpResultBuilder.cs` | New `FormatBatchResult` method (~20 lines) | Low — additive |
| `CommandExamples.cs` | New examples for `batch` command | Low — additive |
| `TwigJsonContext.cs` | `[JsonSerializable]` for `BatchResult`, `BatchItemResult` | Low — additive |

### Backward Compatibility
- Fully backward compatible. No existing commands are modified.
- `twig state`, `twig update`, `twig note` continue to work as before.

### Performance
- **Improvement** for combined operations: 4 HTTP calls instead of 7+ for state+field+note.
- **Multi-item mode**: N×4 HTTP calls (sequential, not parallel). Parallel execution
  would risk ADO rate limiting and is a future optimization (NG).

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `--set` parsing edge cases (values containing `=`) | Medium | Low | Split on first `=` only (`key=val=ue` → key: `key`, value: `val=ue`). Add test coverage. |
| State change + field update conflict (state change rejected but field update valid) | Low | Medium | Fail-fast: validate state transition before building any changes. All-or-nothing per item. |
| ConsoleAppFramework `string[]` parameter handling for `--set` | Low | Medium | Test with ConsoleAppFramework source-gen. Fallback: comma-separated string with custom parsing. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--note` support `--format markdown` for HTML conversion of notes? | Low | ADO comments support HTML natively. Could be added later as enhancement. Current `twig note` does not support markdown format. |
| 2 | Should multi-item mode support per-item state targeting (different states for different items)? | Low | Out of scope for initial implementation. All items in a multi-item batch receive the same state. Consistent with bulk triage use case. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/BatchCommand.cs` | CLI command orchestrating batch operations |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Unit tests for BatchCommand |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Register `twig batch` command with ConsoleAppFramework |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `BatchCommand` as singleton |
| `src/Twig/CommandExamples.cs` | Add usage examples for `batch` command |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add `twig_batch` MCP tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatBatchResult()` method |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `BatchResult`, `BatchItemResult` |
| `tests/Twig.Mcp.Tests/` | Add tests for `twig_batch` MCP tool |

## ADO Work Item Structure

### Issue #1782: Batch state transitions and field updates in twig CLI

**Goal**: Enable combined state transitions, field updates, and notes in a single CLI
invocation with optimized HTTP round-trips.

**Prerequisites**: None

#### Tasks

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1 | **BatchCommand core implementation** — Create `BatchCommand.cs` with single-item flow: parse `--state`/`--set`/`--note` args, resolve item, fetch remote, validate state transition, build combined `FieldChange[]`, PATCH, add comment, auto-push notes, resync cache. Include `BatchResult`/`BatchItemResult` records for output. | `src/Twig/Commands/BatchCommand.cs` | ~250 LoC |
| T-2 | **Multi-item support** — Extend `BatchCommand` with `--id` and `--ids` parameters. Single-item uses interactive conflict resolution; multi-item skips conflict resolution and continues on failure. Aggregate per-item results into `BatchResult`. | `src/Twig/Commands/BatchCommand.cs` | ~80 LoC |
| T-3 | **CLI registration and wiring** — Register `twig batch` command in `Program.cs` with ConsoleAppFramework, register `BatchCommand` in `CommandRegistrationModule.cs`, add usage examples to `CommandExamples.cs`. | `src/Twig/Program.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `src/Twig/CommandExamples.cs` | ~40 LoC |
| T-4 | **MCP `twig_batch` tool** — Add `twig_batch` method to `MutationTools.cs` with headless batch logic. Add `FormatBatchResult()` to `McpResultBuilder`. Add `[JsonSerializable]` attributes for batch result types to `TwigJsonContext`. | `src/Twig.Mcp/Tools/MutationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | ~120 LoC |
| T-5 | **Unit tests** — BatchCommand tests covering: single-item state+field+note, multi-item with failures, state validation rejection, `--set` parsing (including values with `=`), conflict resolution paths, JSON output format, markdown format conversion. MCP twig_batch tests. | `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs`, `tests/Twig.Mcp.Tests/` | ~400 LoC |

#### Acceptance Criteria

- [ ] `twig batch --state Done --set "System.AssignedTo=Alice"` transitions state and updates field in single PATCH
- [ ] `twig batch --ids 1234,1235 --state Done` applies state to both items, continues on failure
- [ ] `twig batch --state Done --note "Completed"` transitions and adds comment
- [ ] `twig batch` with no operations returns exit code 2 with usage message
- [ ] `twig batch -o json` produces structured `BatchResult` JSON
- [ ] MCP `twig_batch` tool works with state, fields array, and note parameters
- [ ] All existing `twig state` and `twig update` tests continue to pass (no regression)
- [ ] HTTP round-trips for state+field+note = 4 (down from 7)

## PR Groups

### PG-1: Core batch command (Tasks T-1, T-2, T-3, T-5-partial)

**Type**: Deep
**Scope**: `src/Twig/Commands/BatchCommand.cs`, `src/Twig/Program.cs`,
`src/Twig/DependencyInjection/CommandRegistrationModule.cs`,
`src/Twig/CommandExamples.cs`,
`tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs`
**Estimated LoC**: ~700
**Estimated Files**: ~5
**Rationale**: The core feature — new CLI command with full test coverage. Reviewable
as a self-contained unit. No cross-project dependencies.
**Successors**: PG-2

### PG-2: MCP tool and serialization (Tasks T-4, T-5-partial)

**Type**: Wide
**Scope**: `src/Twig.Mcp/Tools/MutationTools.cs`,
`src/Twig.Mcp/Services/McpResultBuilder.cs`,
`src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`,
`tests/Twig.Mcp.Tests/`
**Estimated LoC**: ~300
**Estimated Files**: ~4
**Rationale**: MCP layer follows established patterns from existing mutation tools.
Depends on PG-1 merging first to establish the batch semantics. Separately reviewable.
**Successors**: None

