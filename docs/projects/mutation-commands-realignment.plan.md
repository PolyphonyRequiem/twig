# Mutation Commands Realignment — Push-on-Write, Patch, Remove Save/Discard

**Epic:** #2151 — Mutation Commands Realignment  
**Spec:** `docs/specs/mutation-commands.spec.md`  
**Revision:** 0 (Initial draft)

---

## Executive Summary

This epic realigns twig's mutation command surface to a strict **push-on-write** model
where every mutation hits ADO immediately and fails loudly on network errors. The work
adds the `patch` command (atomic multi-field update) and a CLI-level `link batch`
command for bulk link operations, removes the deprecated `save` command and top-level
`discard` command (retaining `seed discard`), removes `link branch` (MCP tool only), and
hardens `note` and `edit` to remove silent offline fallback. Four new MCP tools
(`twig_patch`, `twig_batch` realignment, `twig_link_artifact` already exists,
`twig_link_batch`) complete the agent-facing API. The existing `batch` CLI command and
`twig_batch` MCP tool are already implemented and need only minor alignment with the
spec's `--ids`-based multi-item semantics.

---

## Background

### Current Architecture

Twig's mutation commands split into three tiers:

1. **Direct-push commands** (`state`, `update`, `batch`): Already push-on-write. Fetch
   the remote revision, conflict-resolve, PATCH to ADO, auto-push pending notes, resync
   cache. These are spec-compliant.

2. **Hybrid commands** (`note`, `edit`): Push when online, fall back to local staging
   when ADO is unreachable. The spec requires these to fail loudly instead.

3. **Legacy staging commands** (`save`, `discard`): Exist to flush/clear the local
   staging layer. With push-on-write, they become vestigial.

### MCP Layer

| MCP Tool | Status | Notes |
|----------|--------|-------|
| `twig_state` | ✅ Exists | Spec-compliant |
| `twig_update` | ✅ Exists | Spec-compliant |
| `twig_note` | ✅ Exists | Has offline fallback — needs hardening |
| `twig_discard` | ❌ Remove | No local staging for non-seeds |
| `twig_sync` | ✅ Exists | Spec-compliant |
| `twig_patch` | ❌ Missing | New: atomic multi-field |
| `twig_batch` | ✅ Exists | Already uses graph-based execution; spec wants `--ids` style — evaluate alignment |
| `twig_link` | ✅ Exists | Existing generic link tool |
| `twig_link_artifact` | ✅ Exists | Spec-compliant |
| `twig_link_branch` | ❌ Remove | Git integration removal |
| `twig_link_batch` | ❌ Missing | New: bulk link ops |

### Call-Site Audit — `SaveCommand`

`SaveCommand` and its interface `IPendingChangeFlusher` are used by:

| File | Method/Usage | Impact |
|------|-------------|--------|
| `src/Twig/Commands/SaveCommand.cs` | `ExecuteAsync` | **Delete entirely** |
| `src/Twig/Commands/IPendingChangeFlusher.cs` | Interface | **Keep** — used by `SyncCommand` |
| `src/Twig/Commands/PendingChangeFlusher.cs` | Implementation | **Keep** — used by `SyncCommand` |
| `src/Twig/Commands/SyncCommand.cs` | `FlushAllAsync` | No change — still uses flusher |
| `src/Twig/Program.cs` L663-668 | `Save` method, deprecated | **Delete registration** |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` L121 | DI registration | **No change** — registers `IPendingChangeFlusher`, not `SaveCommand` |
| `tests/Twig.Cli.Tests/Commands/SaveCommand*.cs` | 5 test files | **Delete all** |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Tests edit→save flow | **Delete** |
| `src/Twig/Program.cs` L1005 | Help group "save" | **Remove from help** |

### Call-Site Audit — `DiscardCommand`

| File | Method/Usage | Impact |
|------|-------------|--------|
| `src/Twig/Commands/DiscardCommand.cs` | `ExecuteAsync` | **Delete entirely** |
| `src/Twig/Program.cs` L655-661 | `Discard` method | **Delete registration** |
| `src/Twig/Program.cs` L961 | Help group "discard" | **Remove from help** |
| `src/Twig/Program.cs` L1086-1087 | Help text | **Remove lines** |
| `tests/Twig.Cli.Tests/Commands/DiscardCommandTests.cs` | Tests | **Delete** |
| `src/Twig.Mcp/Tools/MutationTools.cs` L229-273 | `twig_discard` | **Delete tool** |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsDiscardTests.cs` | Tests | **Delete** |

### Call-Site Audit — `link branch` (MCP)

| File | Method/Usage | Impact |
|------|-------------|--------|
| `src/Twig.Mcp/Tools/CreationTools.cs` L201-222 | `twig_link_branch` | **Delete tool** |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` L39 | `BranchLinkService?` | **Keep** — used by `SeedPublishCommand --link-branch` |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBranchTests.cs` | Tests | **Delete** |

### Call-Site Audit — `NoteCommand` Offline Fallback

| File | Method/Usage | Impact |
|------|-------------|--------|
| `src/Twig/Commands/NoteCommand.cs` L117-140 | try/catch offline → StageLocally | **Remove fallback, propagate error** |
| `src/Twig.Mcp/Tools/MutationTools.cs` L197-208 | try/catch offline → stage pending | **Remove fallback, return error** |

### Call-Site Audit — `EditCommand` Staging Fallback

| File | Method/Usage | Impact |
|------|-------------|--------|
| `src/Twig/Commands/EditCommand.cs` L141-148 | catch → StageLocallyAsync | **Remove fallback, add retry/abort prompt** |

---

## Problem Statement

1. **Inconsistent mutation semantics**: `note` and `edit` silently succeed when offline
   by staging locally, while `state` and `update` fail loudly. Users don't realize their
   note never reached ADO.

2. **Vestigial commands**: `save` and top-level `discard` exist solely for the local
   staging layer that push-on-write eliminates. They confuse users and agents.

3. **Missing agent-first primitives**: Agents need `patch` (atomic multi-field) and
   `link batch` (bulk link ops) to work efficiently. Single-field `update` forces
   multiple round-trips.

4. **Dead git integration**: `link branch` MCP tool is being removed as part of the git
   integration cleanup — agents should use `link artifact` with vstfs:// URIs.

---

## Goals and Non-Goals

### Goals

- **G1**: All mutation commands fail loudly on network errors (no silent local staging)
- **G2**: Remove `save` command entirely; remove top-level `discard` (keep `seed discard`)
- **G3**: Add `patch` CLI command and `twig_patch` MCP tool for atomic multi-field updates
- **G4**: Add `link batch` CLI command and `twig_link_batch` MCP tool for bulk link operations
- **G5**: Remove `twig_link_branch` MCP tool and `twig_discard` MCP tool
- **G6**: Add telemetry to all link commands (`link parent`, `link unparent`, `link reparent`, `link artifact`)
- **G7**: `edit` command shows all populated fields (not just Title/State/AssignedTo)

### Non-Goals

- **NG1**: Changing `seed discard` — seed lifecycle is out of scope
- **NG2**: Modifying `state` or `update` commands — already spec-compliant
- **NG3**: Changing `twig_batch` MCP tool (already uses graph-based execution engine, not `--ids`)
- **NG4**: Removing `BranchLinkService` from domain — still used by `seed publish --link-branch`
- **NG5**: Changing the `batch` CLI command — already implements spec-compliant behavior

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `twig patch --json '{...}' [--id N] [--format markdown]` atomically patches multiple fields |
| FR-2 | `twig patch --stdin` reads JSON from stdin |
| FR-3 | `twig link batch --json '[...]'` processes bulk link operations |
| FR-4 | `twig link batch --stdin` reads JSON from stdin |
| FR-5 | `twig note` exits 1 when ADO is unreachable (no staging fallback) |
| FR-6 | `twig edit` on push failure offers retry/abort prompt (no staging fallback) |
| FR-7 | `twig edit` generates editor content with ALL populated fields |
| FR-8 | `twig save` command registration is removed |
| FR-9 | Top-level `twig discard` command registration is removed |
| FR-10 | `twig_patch` MCP tool supports atomic multi-field mutation |
| FR-11 | `twig_link_batch` MCP tool supports bulk link operations |
| FR-12 | `twig_discard` MCP tool is removed |
| FR-13 | `twig_link_branch` MCP tool is removed |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | All new commands emit telemetry per spec |
| NFR-2 | All new code is AOT-compatible (no reflection, use `TwigJsonContext`) |
| NFR-3 | Zero new compiler warnings (`TreatWarningsAsErrors=true`) |
| NFR-4 | All removed commands produce clear error messages if accidentally invoked |

---

## Proposed Design

### Architecture Overview

The changes span four layers:

```
┌─────────────────────────────────────────────┐
│  CLI Layer (Program.cs + Commands/)          │
│  - Remove save, discard registrations        │
│  - Add patch, link batch registrations       │
│  - Harden note, edit (remove fallbacks)      │
│  - Add telemetry to link commands            │
├─────────────────────────────────────────────┤
│  MCP Layer (Twig.Mcp/Tools/)                │
│  - Remove twig_discard, twig_link_branch     │
│  - Add twig_patch, twig_link_batch           │
│  - Harden twig_note (remove fallback)        │
├─────────────────────────────────────────────┤
│  Infrastructure (Ado/, Serialization/)       │
│  - No interface changes needed               │
│  - ConflictRetryHelper reused as-is          │
├─────────────────────────────────────────────┤
│  Domain (Interfaces/, Services/)             │
│  - No changes needed                         │
└─────────────────────────────────────────────┘
```

### Key Components

#### 1. `PatchCommand` (new)

```csharp
public sealed class PatchCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextReader? stdinReader = null,
    TextWriter? stderr = null)
```

Accepts JSON via `--json` or `--stdin`, parses into `Dictionary<string, string>`,
converts markdown values if `--format markdown`, builds `FieldChange[]`, calls
`ConflictRetryHelper.PatchWithRetryAsync`, auto-pushes notes, resyncs cache.

Reuses the same `ProcessItemAsync` core flow from `BatchCommand` but scoped to
a single item with structured JSON input instead of `--set key=value` pairs.

#### 2. `LinkBatchCommand` (new)

```csharp
public sealed class LinkBatchCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorFactory syncCoordinatorFactory,
    OutputFormatterFactory formatterFactory,
    TextReader? stdinReader = null,
    TextWriter? stderr = null)
```

Accepts JSON array of link operations, dispatches each to the appropriate
link logic (parent/unparent/reparent/artifact), deduplicates resync targets.

#### 3. `NoteCommand` Hardening

Remove the `catch (Exception ex) → StageLocallyAsync` fallback in both:
- `NoteCommand.cs` (CLI) — propagate the exception, exit 1
- `MutationTools.cs` `twig_note` (MCP) — return `McpResultBuilder.ToError()`

#### 4. `EditCommand` Hardening

Remove the staging fallback. Add a retry/abort loop:
```
Push failed: {error}. Retry edit, or abort? (r/A)
```
On retry, re-open editor. On abort, exit 1.

Expand editor content generation to include ALL populated fields from
`item.Fields` dictionary (currently only Title/State/AssignedTo).

### Data Flow — `twig patch`

```
User input (--json / --stdin)
  │
  ├─ Parse JSON → Dictionary<string, string>
  ├─ Validate fields exist
  ├─ Convert markdown values (if --format markdown)
  │
  ├─ Resolve work item (--id or active)
  ├─ Fetch remote revision
  ├─ Conflict detection + resolution
  │
  ├─ Build FieldChange[] from all fields
  ├─ PatchWithRetryAsync (single atomic PATCH)
  ├─ Auto-push pending notes
  ├─ Resync cache
  │
  └─ Output confirmation
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| `patch` reuses `ConflictRetryHelper` | Same retry-once pattern as `update`/`state`/`batch` |
| `link batch` does NOT reuse `LinkCommand` directly | `LinkCommand` is CLI-coupled (console output). Extract logic into shared helpers. |
| MCP `twig_note` removes offline fallback entirely | Spec mandates push-on-write with no silent fallback. Seeds still stage. |
| Keep `IPendingChangeFlusher` | Still needed by `SyncCommand` for residual pending notes |
| `edit` retry loop is interactive-only | No MCP equivalent needed (agents use `update`/`patch`) |

---

## Dependencies

### External
- No new NuGet packages required
- No ADO API changes needed (all operations use existing `PatchAsync`, `AddLinkAsync`, etc.)

### Internal
- `ConflictRetryHelper` — reused by `PatchCommand`
- `AutoPushNotesHelper` — reused by `PatchCommand`
- `LinkCommand` helper methods — extracted for `LinkBatchCommand` reuse
- `McpResultBuilder` — extended with `FormatPatch` and `FormatLinkBatch` methods

### Sequencing
- Issue 1 (Remove save/discard) can be done independently
- Issue 2 (Harden note/edit) can be done independently
- Issue 3 (Add patch) can be done independently
- Issue 4 (Add link batch) depends on Issue 5 (link telemetry) for consistency
- Issue 5 (Link telemetry) can be done independently

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Users rely on `save` command in scripts | Low | Medium | `save` already shows deprecation hint since v0.50+; remove cleanly |
| Agents call `twig_discard` | Low | Low | MCP tool removal causes clear error; agents adapt |
| `note` offline failure breaks CI scripts | Medium | Medium | Document the behavioral change; `sync` still pushes residual notes |
| `edit` all-fields generates huge editor content | Low | Low | Field list is typically 10-20 entries; manageable |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should `twig_batch` MCP tool be aligned to `--ids` style from spec, or keep the current graph-based engine? | Low | Current graph engine is more capable; spec describes a simpler model. Recommend keeping graph engine as-is since it's already implemented and tested. |
| OQ-2 | Should `seed publish --link-branch` be removed in this epic? | Low | Spec removes `link branch` CLI command but `seed publish` uses `BranchLinkService` independently. Recommend keeping it — separate concern. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/PatchCommand.cs` | Atomic multi-field update CLI command |
| `src/Twig/Commands/LinkBatchCommand.cs` | Bulk link operations CLI command |
| `tests/Twig.Cli.Tests/Commands/PatchCommandTests.cs` | Unit tests for PatchCommand |
| `tests/Twig.Cli.Tests/Commands/LinkBatchCommandTests.cs` | Unit tests for LinkBatchCommand |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsPatchTests.cs` | Unit tests for twig_patch MCP tool |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBatchTests.cs` | Unit tests for twig_link_batch MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Add `patch` and `link batch` registrations; remove `save`, `discard` registrations; update help text |
| `src/Twig/Commands/NoteCommand.cs` | Remove offline staging fallback; propagate ADO errors as exit 1 |
| `src/Twig/Commands/EditCommand.cs` | Remove staging fallback; add retry/abort loop; expand field generation to all populated fields |
| `src/Twig/Commands/LinkCommand.cs` | Add telemetry instrumentation to parent/unparent/reparent |
| `src/Twig/Commands/ArtifactLinkCommand.cs` | Add telemetry instrumentation |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Remove `twig_discard`; add `twig_patch`; harden `twig_note` (remove fallback) |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Remove `twig_link_branch`; add `twig_link_batch` |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatPatch`, `FormatLinkBatch` methods |
| `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | Update tests for offline-failure behavior |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Rename/rewrite as EditCommand-only tests (remove save refs) |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsNoteTests.cs` | Update tests for offline-failure behavior |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/SaveCommand.cs` | Push-on-write removes need for explicit save |
| `src/Twig/Commands/DiscardCommand.cs` | No local staging for non-seeds; seed discard remains |
| `tests/Twig.Cli.Tests/Commands/SaveCommandContinueOnFailureTests.cs` | SaveCommand removed |
| `tests/Twig.Cli.Tests/Commands/SaveCommandDeprecationTests.cs` | SaveCommand removed |
| `tests/Twig.Cli.Tests/Commands/SaveCommandNotesOnlyBypassTests.cs` | SaveCommand removed |
| `tests/Twig.Cli.Tests/Commands/SaveCommandScopingTests.cs` | SaveCommand removed |
| `tests/Twig.Cli.Tests/Commands/SaveCommandTestBase.cs` | SaveCommand removed |
| `tests/Twig.Cli.Tests/Commands/DiscardCommandTests.cs` | DiscardCommand removed |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsDiscardTests.cs` | twig_discard removed |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkBranchTests.cs` | twig_link_branch removed |

---

## ADO Work Item Structure

**Parent Epic:** #2151 — Mutation Commands Realignment

### Issue 1: Remove Save and Discard Commands

**Goal:** Remove the top-level `save` command entirely and the top-level `discard`
command (retaining `seed discard`), along with their MCP counterparts.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1.1 | Delete `SaveCommand.cs` and remove its Program.cs registration (the `Save` method and the `[Hidden]` attribute) | `SaveCommand.cs`, `Program.cs` | S |
| T1.2 | Delete `DiscardCommand.cs` and remove its Program.cs registration (the `Discard` method) | `DiscardCommand.cs`, `Program.cs` | S |
| T1.3 | Remove `twig_discard` from `MutationTools.cs` (lines 229-273) | `MutationTools.cs` | S |
| T1.4 | Update `GroupedHelp` command lists: remove "save" from hidden list, remove "discard" from work items group, update help text | `Program.cs` | S |
| T1.5 | Delete all SaveCommand and DiscardCommand test files | 7 test files | S |
| T1.6 | Delete `MutationToolsDiscardTests.cs` | Test file | S |

**Acceptance Criteria:**
- [ ] `twig save` produces a clear "unknown command" error
- [ ] `twig discard` produces a clear "unknown command" error
- [ ] `twig seed discard` still works correctly
- [ ] `twig_discard` MCP tool no longer appears in tool list
- [ ] `twig sync` still flushes pending changes (IPendingChangeFlusher untouched)
- [ ] Solution builds with zero warnings

### Issue 2: Harden Note and Edit Commands (Push-on-Write)

**Goal:** Remove offline staging fallback from `note` and `edit` commands so they
fail loudly when ADO is unreachable, per the push-on-write spec.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T2.1 | Remove offline fallback in `NoteCommand.cs`: delete the `catch` block that calls `StageLocallyAsync`, let exception propagate to exit 1 with error message | `NoteCommand.cs` | S |
| T2.2 | Remove offline fallback in MCP `twig_note`: change catch block to return `McpResultBuilder.ToError()` instead of staging locally | `MutationTools.cs` | S |
| T2.3 | Remove staging fallback in `EditCommand.cs`: delete catch block that calls `StageLocallyAsync`, replace with retry/abort prompt loop | `EditCommand.cs` | M |
| T2.4 | Expand `EditCommand` field generation to use all populated `item.Fields` entries (not just Title/State/AssignedTo) | `EditCommand.cs` | M |
| T2.5 | Update `NoteCommandTests.cs` to verify exit 1 on ADO failure (not successful staging) | `NoteCommandTests.cs` | S |
| T2.6 | Update `MutationToolsNoteTests.cs` for offline-failure behavior | `MutationToolsNoteTests.cs` | S |
| T2.7 | Update or rewrite `EditSaveCommandTests.cs` removing save-related tests, adding retry/abort tests and all-fields tests | `EditSaveCommandTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig note --text "test"` exits 1 when ADO is unreachable
- [ ] `twig note --text "test"` for seed items still stages locally (unchanged)
- [ ] `twig edit` on push failure shows "Retry edit, or abort? (r/A)" prompt
- [ ] `twig edit` generates editor content with all populated fields
- [ ] MCP `twig_note` returns error (not success with isPending) when ADO is down
- [ ] All tests pass

### Issue 3: Add Patch Command and MCP Tool

**Goal:** Implement `twig patch` CLI command and `twig_patch` MCP tool for atomic
multi-field updates on a single work item.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T3.1 | Create `PatchCommand.cs` with JSON input parsing, markdown conversion, conflict retry, and output formatting | `PatchCommand.cs` | L |
| T3.2 | Register `patch` command in `Program.cs` with `--json`, `--stdin`, `--format`, `--id`, `--output` parameters | `Program.cs` | S |
| T3.3 | Add `twig_patch` MCP tool to `MutationTools.cs` with `fields` JSON object, `format`, and `workspace` parameters | `MutationTools.cs` | M |
| T3.4 | Add `FormatPatch` method to `McpResultBuilder.cs` | `McpResultBuilder.cs` | S |
| T3.5 | Add telemetry to `PatchCommand` (`command=patch`, `field_count`, `exit_code`, `duration_ms`) | `PatchCommand.cs` | S |
| T3.6 | Create `PatchCommandTests.cs` with tests for: JSON parsing, stdin, markdown conversion, conflict handling, validation errors | `PatchCommandTests.cs` | M |
| T3.7 | Create `MutationToolsPatchTests.cs` with MCP-level tests | `MutationToolsPatchTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig patch --json '{"System.Title":"New"}' ` updates field atomically
- [ ] `twig patch --stdin` reads JSON from stdin and applies
- [ ] `twig patch --format markdown` converts string values to HTML
- [ ] `twig patch` with invalid JSON exits 2
- [ ] `twig patch` with no input exits 2
- [ ] `twig patch` with both `--json` and `--stdin` exits 2
- [ ] `twig_patch` MCP tool works with field map input
- [ ] Conflict retry works (retry once, fail on second)
- [ ] Telemetry emits `field_count`

### Issue 4: Add Link Batch Command and MCP Tool

**Goal:** Implement `twig link batch` CLI command and `twig_link_batch` MCP tool
for bulk link operations.

**Prerequisites:** Issue 5 (Link Telemetry) — for telemetry consistency

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T4.1 | Create `LinkBatchCommand.cs` with JSON parsing, per-operation dispatch, deduplicated resync, aggregate output | `LinkBatchCommand.cs` | L |
| T4.2 | Register `link batch` command in `Program.cs` with `--json`, `--stdin`, `--output` parameters | `Program.cs` | S |
| T4.3 | Add `twig_link_batch` MCP tool to `CreationTools.cs` with `operations` array parameter | `CreationTools.cs` | M |
| T4.4 | Add `FormatLinkBatch` method to `McpResultBuilder.cs` | `McpResultBuilder.cs` | S |
| T4.5 | Add telemetry to `LinkBatchCommand` (`command=link-batch`, `operation_count`, `succeeded_count`) | `LinkBatchCommand.cs` | S |
| T4.6 | Create `LinkBatchCommandTests.cs` with tests for: parent, unparent, reparent, artifact ops, mixed success/failure | `LinkBatchCommandTests.cs` | M |
| T4.7 | Create `CreationToolsLinkBatchTests.cs` with MCP-level tests | `CreationToolsLinkBatchTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig link batch --json '[{"op":"parent","itemId":1,"targetId":2}]'` creates parent link
- [ ] Mixed success/failure: exit 1 if any fail, per-op results in output
- [ ] JSON output matches spec format
- [ ] `twig_link_batch` MCP tool dispatches operations correctly
- [ ] Resync targets are deduplicated
- [ ] Telemetry emits `operation_count` and `succeeded_count`

### Issue 5: Link Command Telemetry and Cleanup

**Goal:** Add telemetry instrumentation to all link commands and remove
`twig_link_branch` MCP tool.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T5.1 | Add telemetry to `LinkCommand.ParentAsync` (`command=link-parent`, `exit_code`, `duration_ms`) | `LinkCommand.cs` | S |
| T5.2 | Add telemetry to `LinkCommand.UnparentAsync` (`command=link-unparent`, `exit_code`, `duration_ms`) | `LinkCommand.cs` | S |
| T5.3 | Add telemetry to `LinkCommand.ReparentAsync` (`command=link-reparent`, `exit_code`, `duration_ms`) | `LinkCommand.cs` | S |
| T5.4 | Add telemetry to `ArtifactLinkCommand.ExecuteAsync` (`command=link-artifact`, `exit_code`, `duration_ms`) | `ArtifactLinkCommand.cs` | S |
| T5.5 | Remove `twig_link_branch` from `CreationTools.cs` (lines 201-222) | `CreationTools.cs` | S |
| T5.6 | Delete `CreationToolsLinkBranchTests.cs` | Test file | S |
| T5.7 | Update `LinkCommandTests.cs` to verify telemetry is emitted for parent/unparent/reparent | `LinkCommandTests.cs` | S |
| T5.8 | Update `ArtifactLinkCommandTests.cs` to verify telemetry | `ArtifactLinkCommandTests.cs` | S |
| T5.9 | Add `GroupedHelp` entry for `link batch` in the Work Items group | `Program.cs` | S |

**Acceptance Criteria:**
- [ ] All link commands emit telemetry with correct command names
- [ ] `twig_link_branch` MCP tool no longer appears in tool list
- [ ] Telemetry property keys pass allowlist check
- [ ] Help text includes `link batch` command

---

## PR Groups

### PG-1: Remove Save, Discard, and Link Branch (Wide)

**Issues:** Issue 1, Issue 5 (T5.5-T5.6 only)  
**Classification:** Wide — many files, mechanical deletions  
**Estimated LoC:** ~400 (mostly deletions)  
**Estimated Files:** ~15  
**Successor:** None (independent)

**Contents:**
- Delete `SaveCommand.cs`, `DiscardCommand.cs` and all their test files
- Remove `twig_discard` from MCP `MutationTools.cs`
- Remove `twig_link_branch` from MCP `CreationTools.cs`
- Delete `MutationToolsDiscardTests.cs`, `CreationToolsLinkBranchTests.cs`
- Update `Program.cs` registrations and help text
- Clean up any orphaned `using` statements

### PG-2: Harden Note and Edit + Link Telemetry (Deep)

**Issues:** Issue 2, Issue 5 (T5.1-T5.4, T5.7-T5.9)  
**Classification:** Deep — behavioral changes in existing commands  
**Estimated LoC:** ~600  
**Estimated Files:** ~12  
**Successor:** PG-1 (merge PG-1 first to avoid conflicts on Program.cs)

**Contents:**
- Harden `NoteCommand` and MCP `twig_note` (remove offline fallback)
- Harden `EditCommand` (remove staging fallback, add retry/abort, expand fields)
- Add telemetry to `LinkCommand` and `ArtifactLinkCommand`
- Update/add tests for all behavioral changes
- Add `link batch` help entry

### PG-3: Add Patch and Link Batch Commands (Deep)

**Issues:** Issue 3, Issue 4  
**Classification:** Deep — new command implementations  
**Estimated LoC:** ~1500  
**Estimated Files:** ~12  
**Successor:** PG-2 (merge PG-2 first for clean Program.cs state)

**Contents:**
- Create `PatchCommand.cs` and register in Program.cs
- Create `LinkBatchCommand.cs` and register in Program.cs
- Add `twig_patch` and `twig_link_batch` MCP tools
- Add `FormatPatch` and `FormatLinkBatch` to McpResultBuilder
- Create all new test files
- Add telemetry to both new commands

---

## References

- [Mutation Commands Spec](../specs/mutation-commands.spec.md) — authoritative behavioral spec
- [PR Grouping Guidelines](../../.github/instructions/pr-grouping.instructions.md) — PR sizing heuristics

---

## Execution Plan

### PR Group Summary

| Group | Name | Issues / Tasks | Dependencies | Type | Est. LoC |
|-------|------|----------------|--------------|------|----------|
| PG-1 | remove-save-discard-linkbranch | Issue 1 (T1.1–T1.6), Issue 5 (T5.5–T5.6) | None | Wide | ~400 |
| PG-2 | harden-note-edit-link-telemetry | Issue 2 (T2.1–T2.7), Issue 5 (T5.1–T5.4, T5.7–T5.9) | PG-1 | Deep | ~600 |
| PG-3 | add-patch-linkbatch-commands | Issue 3 (T3.1–T3.7), Issue 4 (T4.1–T4.7) | PG-2 | Deep | ~1500 |

### Execution Order

**PG-1 → PG-2 → PG-3** (sequential).

1. **PG-1** is a mechanical cleanup: delete `SaveCommand`, `DiscardCommand`, `twig_discard`,
   and `twig_link_branch`, along with all their tests. Program.cs registrations and help
   entries are pruned. No logic changes. Merging this first establishes a clean baseline
   with no legacy surface area and eliminates merge-conflict risk on `Program.cs` for
   downstream groups.

2. **PG-2** builds on the clean baseline to harden behavioral semantics: remove offline
   fallbacks from `NoteCommand` (CLI + MCP), add the retry/abort loop and all-fields
   generation to `EditCommand`, and instrument `LinkCommand` / `ArtifactLinkCommand` with
   telemetry. Tests are updated or rewritten to verify the new error-propagation behavior.
   The `link batch` help entry is added here so PG-3's new command is already listed when
   its PR lands.

3. **PG-3** adds the two new commands (`PatchCommand`, `LinkBatchCommand`) with their MCP
   counterparts (`twig_patch`, `twig_link_batch`), `McpResultBuilder` format helpers, and
   full test suites. It depends on PG-2 to avoid duplicate Program.cs and
   `McpResultBuilder.cs` conflicts.

### Validation Strategy

**PG-1 — Self-containment check:**
- `dotnet build` (zero warnings)
- `dotnet test` — all SaveCommand / DiscardCommand tests are deleted; remaining suite passes
- `twig save` → "unknown command" error; `twig discard` → "unknown command" error
- `twig seed discard` still passes its tests
- `twig sync` tests still pass (IPendingChangeFlusher untouched)

**PG-2 — Self-containment check:**
- `dotnet build` (zero warnings)
- `dotnet test` — NoteCommand offline tests verify exit 1; EditCommand retry/abort tests pass; link telemetry tests pass
- Manual smoke: `twig note --text x` with mocked offline → exits 1 with error message

**PG-3 — Self-containment check:**
- `dotnet build` (zero warnings)
- `dotnet test` — PatchCommand and LinkBatchCommand test suites pass; MCP tool tests pass
- `twig patch --json '{"System.Title":"x"}'` applies field atomically
- `twig link batch --json '[...]'` processes operations and deduplicates resync
