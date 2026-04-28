# Delete Command — Guarded Work Item Deletion with Link Safety Checks

**Epic:** #2172  
**Status:** ✅ Done  
**Revision:** 1  

---

## Executive Summary

Add a `twig delete <id>` CLI command and `twig_delete` MCP tool for permanently removing Azure DevOps work items. Because deletion is irreversible, the feature implements multiple safety layers: a link check that refuses deletion when any ADO links exist (parent, child, related, dependency), interactive confirmation with `--force` bypass, audit trail via parent note, and prominent UX messaging that recommends closing items instead of deleting them. The design follows existing command patterns (DiscardCommand, StateCommand) with a new `DeleteAsync` method on `IAdoWorkItemService` backed by the ADO REST `DELETE /wit/workitems/{id}` endpoint.

## Background

### Current State

Twig currently has no way to delete work items from ADO. When a user accidentally creates a work item (via `twig new`, `twig seed publish`, or directly in the ADO web UI), the only recourse is to transition it to a terminal state (Closed/Removed) via `twig state`. This is actually the recommended path in most cases—deletion is destructive and removes audit history.

However, there are legitimate cases where deletion is needed:
- Test/scratch items created during development
- Accidentally duplicated items with no meaningful history
- Items created in the wrong project that can't be moved

### Architecture Context

**Command layer** (`src/Twig/Commands/`): Commands are `sealed` classes with primary constructor DI. They receive domain interfaces, format output via `OutputFormatterFactory`, and return `int` exit codes. Destructive commands (DiscardCommand) use `IConsoleInput` for interactive confirmation with `--yes`/`--force` bypass.

**ADO service layer** (`IAdoWorkItemService` → `AdoRestClient`): All ADO REST calls go through `AdoRestClient.SendAsync()` which handles auth, throttling, error mapping, and retry. The interface currently has Fetch, Patch, Create, AddComment, AddLink, RemoveLink, and Query methods—but no Delete.

**MCP layer** (`src/Twig.Mcp/Tools/MutationTools.cs`): MCP tools mirror CLI commands with workspace resolution via `WorkspaceResolver`. They return `CallToolResult` via `McpResultBuilder` helpers.

**Link model**: `WorkItemLink` is a value object with `(SourceId, TargetId, LinkType)`. Links are fetched via `IAdoWorkItemService.FetchWithLinksAsync()` which returns both the item and its non-hierarchy links. Parent/child hierarchy links are tracked via `ParentId` on the `WorkItem` aggregate.

### Call-Site Audit

This feature introduces a **new** method on `IAdoWorkItemService` (`DeleteAsync`) and a **new** command class. No existing call sites are affected. The only cross-cutting concern is adding the command to:

| Location | What changes | Impact |
|----------|--------------|--------|
| `IAdoWorkItemService.cs` | New `DeleteAsync` method | Interface extension—no existing callers |
| `AdoRestClient.cs` | Implement `DeleteAsync` | New HTTP DELETE call |
| `CommandRegistrationModule.cs` | Register `DeleteCommand` | DI registration |
| `Program.cs` → `TwigCommands` | Add `Delete` method | Command routing |
| `Program.cs` → `GroupedHelp` | Add to `KnownCommands` + help text | Help display |
| `MutationTools.cs` | Add `twig_delete` tool | MCP parity |
| `Mcp/Program.cs` | No change needed | `MutationTools` already registered via `WithTools<MutationTools>()` |
| `HintEngine.cs` | Add "delete" case | Contextual hints |
| `McpResultBuilder.cs` | Add `FormatDelete` helper | MCP response formatting |

## Problem Statement

Users cannot permanently remove erroneously created ADO work items through twig. While closing/removing items is preferred, there are cases (test items, duplicates, wrong-project items) where full deletion is needed. The lack of a delete command forces users to leave the twig workflow and use the ADO web UI directly.

## Goals

1. **Safe deletion**: Provide a `twig delete <id>` command that permanently deletes a single ADO work item with multiple safety guards.
2. **Link safety**: Refuse deletion when the item has any ADO links (parent, child, related, predecessor/successor), requiring users to unlink first.
3. **Audit trail**: Log a note on the parent item (if any) before deletion, recording what was deleted.
4. **UX guidance**: All help text, error messages, and confirmation prompts recommend closing items instead of deleting.
5. **MCP parity**: Provide `twig_delete` MCP tool with equivalent guards.
6. **No batch support**: Explicitly single-item only—no `--all` flag.

## Non-Goals

1. **Recycle bin / soft delete**: ADO's delete is permanent (or uses ADO's built-in recycle bin). We don't implement our own soft-delete layer.
2. **Batch deletion**: No `--all` or multi-ID support. Intentionally friction-heavy.
3. **Seed deletion**: Seeds (local, unpublished items) are handled by `twig seed discard`. This command targets published ADO items only.
4. **Link auto-removal**: We will not automatically remove links before deletion. Users must explicitly unlink first.
5. **Undo/restore**: No undo capability. ADO's recycle bin (if enabled) is the only recovery path.

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `twig delete <id>` permanently deletes work item `<id>` from ADO |
| FR-2 | Before deletion, fetch the item with links from ADO (fresh, not cached) |
| FR-3 | If the item has any links (parent, child, related, predecessor, successor, artifact), refuse deletion with a descriptive error listing link count and types |
| FR-4 | Display confirmation prompt showing item ID, title, type, state, and link count. Require user to type "yes" (not just "y") given the destructive nature |
| FR-5 | `--force` flag bypasses the interactive confirmation (for scripting) |
| FR-6 | After successful deletion, remove the item from the local cache |
| FR-7 | If the item had a parent, add a note to the parent recording the deletion (best-effort) |
| FR-8 | All help text and error messages include guidance to use `twig state Closed` or `twig state Removed` instead |
| FR-9 | `twig_delete` MCP tool with same link check and confirmation via structured response |
| FR-10 | Telemetry event with command name, exit code, duration (no item identifiers) |
| FR-11 | Seed guard: refuse to delete seed items (IsSeed), directing to `twig seed discard` |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: no reflection, source-generated JSON |
| NFR-2 | Warnings-as-errors clean |
| NFR-3 | All new types added to `TwigJsonContext` if serialized |
| NFR-4 | Tests cover all guard paths, happy path, and edge cases |

## Proposed Design

### Architecture Overview

```
CLI Layer                          MCP Layer
┌──────────────────┐               ┌──────────────────┐
│  DeleteCommand   │               │  MutationTools    │
│  (twig delete)   │               │  (twig_delete)    │
└────────┬─────────┘               └────────┬─────────┘
         │                                  │
         │  uses                            │  uses
         ▼                                  ▼
┌──────────────────────────────────────────────────────┐
│              IAdoWorkItemService.DeleteAsync()        │
│              IAdoWorkItemService.FetchWithLinksAsync() │
│              IWorkItemRepository.DeleteByIdAsync()     │
└──────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────┐
│  ADO REST API    │
│  DELETE /wit/    │
│  workitems/{id}  │
└──────────────────┘
```

### Key Components

#### 1. `IAdoWorkItemService.DeleteAsync(int id, CancellationToken ct)`

New method on the ADO service interface. Calls `DELETE {orgUrl}/{project}/_apis/wit/workitems/{id}?api-version=7.1`. The `destroy` query parameter is intentionally omitted—ADO's default behavior sends items to the recycle bin, which provides a recovery path.

**Implementation in `AdoRestClient`:**
- Uses existing `SendAsync()` infrastructure for auth, throttling, error handling
- HTTP DELETE method (no request body needed)
- Returns void (204 No Content on success)

#### 2. `DeleteCommand` (CLI)

New command class following the `DiscardCommand` pattern for destructive operations.

**Constructor dependencies:**
- `ActiveItemResolver` — resolve item by ID
- `IAdoWorkItemService` — fetch-with-links + delete
- `IWorkItemRepository` — cache cleanup
- `IWorkItemLinkRepository` — local link cache cleanup
- `IConsoleInput` — interactive confirmation
- `OutputFormatterFactory` — output formatting
- `HintEngine` — contextual hints
- `IPromptStateWriter` — prompt state refresh
- `ITelemetryClient` — telemetry

**Execution flow:**
1. Resolve item from cache or ADO by ID
2. **Seed guard**: If `item.IsSeed`, error with redirect to `twig seed discard`
3. **Fresh fetch**: Call `FetchWithLinksAsync(id)` to get current server state + links
4. **Link guard**: If any links exist (parent, children, non-hierarchy links), refuse with detailed error showing link count and types. Message includes guidance to remove links first.
5. **Children guard**: Call `FetchChildrenAsync(id)` to check for child items not captured by link relations
6. **Confirmation**: Display item details (ID, title, type, state) + "Consider closing instead" warning. Require explicit "yes" input (not "y") unless `--force` is passed. Non-TTY environments without `--force` are rejected.
7. **Audit trail**: If item has a parent, best-effort `AddCommentAsync` on parent noting deletion
8. **Delete**: Call `DeleteAsync(id)` 
9. **Cache cleanup**: Remove from local cache via `DeleteByIdAsync` and clean up link cache
10. **Prompt state**: Refresh prompt state
11. **Output**: Success message with closing-recommendation hint

#### 3. `twig_delete` MCP Tool

Added to `MutationTools` class. Follows existing tool patterns.

**MCP-specific behavior:**
- No interactive confirmation (MCP tools are non-interactive)
- Instead, returns a structured "confirmation required" response on first call with `requiresConfirmation: true`, item details, and link status
- Caller must re-invoke with `confirmed: true` parameter to proceed
- Link check still enforced—returns error if links exist

**Parameters:**
- `id` (required): Work item ID to delete
- `confirmed` (optional, default false): Set to true to confirm deletion
- `workspace` (optional): Target workspace

#### 4. `McpResultBuilder.FormatDelete` / `FormatDeleteConfirmation`

New static methods for formatting delete results:
- `FormatDeleteConfirmation(WorkItem item)` — returns item details with `requiresConfirmation: true`
- `FormatDeleted(int id, string title)` — returns deletion success with `deleted: true`

### Data Flow

**CLI Delete Flow:**
```
User → twig delete 1234
  → Resolve item from cache (ActiveItemResolver)
  → Seed guard check
  → FetchWithLinksAsync(1234) from ADO [fresh server state]
  → FetchChildrenAsync(1234) from ADO [check children]
  → Link guard: any links? → Error with details
  → Display confirmation prompt
  → User types "yes"
  → AddCommentAsync(parentId, "Deleted: #1234 ...") [best-effort audit]
  → DeleteAsync(1234) → HTTP DELETE to ADO
  → DeleteByIdAsync(1234) [cache cleanup]
  → Output success + hint
```

**MCP Delete Flow (two-phase):**
```
Agent → twig_delete(id: 1234)
  → Fetch item + links
  → Link guard
  → Return: { requiresConfirmation: true, id: 1234, title: "...", warning: "..." }

Agent → twig_delete(id: 1234, confirmed: true)
  → Re-fetch + re-check links
  → Audit note on parent
  → DeleteAsync(1234)
  → Cache cleanup
  → Return: { deleted: true, id: 1234, title: "..." }
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Require "yes" (not "y") for confirmation | Extra friction for irreversible operation. Matches `rm -i` best practices for destructive commands. |
| Fresh fetch before delete (not cache) | Cache may be stale. Link check must reflect current server state. |
| No `--destroy` parameter | ADO's default delete sends to recycle bin. Permanent destroy is too dangerous for CLI exposure. |
| Link check includes ALL relation types | Parent, child, related, predecessor, successor, artifact links. Any link indicates the item is referenced elsewhere. |
| Two-phase MCP confirmation | MCP tools are non-interactive. Structured confirmation prevents accidental deletion by AI agents. |
| No batch delete | Intentional friction. Deleting multiple items should require multiple explicit commands. |
| Best-effort parent audit | Audit note failure should not block deletion. Parent may have been deleted or may be inaccessible. |
| Explicit ID required (no active-item default) | Unlike other commands, delete does NOT default to the active work item. Forces intentional targeting. |

## Dependencies

### External
- **ADO REST API**: `DELETE /{project}/_apis/wit/workitems/{id}?api-version=7.1` — standard ADO endpoint, no new dependencies

### Internal
- `IAdoWorkItemService` — interface extension
- `AdoRestClient` — implementation
- `ActiveItemResolver` — item resolution
- `IWorkItemRepository.DeleteByIdAsync` — already exists
- `IWorkItemLinkRepository.GetLinksAsync` / `SaveLinksAsync` — already exists

### Sequencing
- No dependencies on other features. Self-contained.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| User accidentally deletes important item | Medium | High | Multi-layer guards: link check, confirmation, "yes" not "y", no batch, hint to close instead |
| ADO recycle bin disabled in org | Low | High | Warning in help text that deletion may be permanent depending on org settings |
| Stale cache after deletion causes confusion | Low | Medium | Cache cleanup immediately after delete; prompt state refresh |
| AI agent mass-deletes via MCP | Low | High | Two-phase confirmation; link guard; no batch support |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `twig delete` also clear any pending changes for the item being deleted? | Low | Likely yes—defensive cleanup. Recommend clearing pending changes during cache cleanup. |
| 2 | Should we support `--destroy` flag for permanent deletion (bypassing recycle bin)? | Low | Recommend no for v1. Can be added later if needed. ADO recycle bin is a useful safety net. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/DeleteCommand.cs` | CLI delete command implementation |
| `tests/Twig.Cli.Tests/Commands/DeleteCommandTests.cs` | CLI command unit tests |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsDeleteTests.cs` | MCP tool unit tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add `DeleteAsync(int id, CancellationToken ct)` method |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Implement `DeleteAsync` via HTTP DELETE |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `DeleteCommand` |
| `src/Twig/Program.cs` | Add `Delete` method to `TwigCommands`, update `GroupedHelp`, add to `KnownCommands`, add `CommandExamples` |
| `src/Twig/Hints/HintEngine.cs` | Add "delete" hint case |
| `src/Twig/CommandExamples.cs` | Add delete command examples |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add `twig_delete` tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatDeleteConfirmation` and `FormatDeleted` methods |
| `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` | Update known command assertions if completeness test exists |

## ADO Work Item Structure

This is an Epic (#2172). The work breaks down into 3 Issues, each with concrete Tasks.

---

### Issue 1: Domain & Infrastructure — DeleteAsync API

**Goal:** Add the `DeleteAsync` method to the ADO service interface and implement it in the REST client.

**Prerequisites:** None

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2172-1 | Add `DeleteAsync(int id, CancellationToken ct)` to `IAdoWorkItemService` interface | `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | S |
| T-2172-2 | Implement `DeleteAsync` in `AdoRestClient` using HTTP DELETE to `/_apis/wit/workitems/{id}?api-version=7.1`. Handle 204 success, 404 (already deleted → idempotent), standard error mapping. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | S |
| T-2172-3 | Add infrastructure unit tests for `DeleteAsync` — success path, 404 idempotent, auth error, offline error | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientDeleteTests.cs` | S |

**Acceptance Criteria:**
- [x] `IAdoWorkItemService` has `DeleteAsync` method
- [x] `AdoRestClient.DeleteAsync` sends HTTP DELETE and handles success/error codes
- [x] Tests verify success, 404-idempotent, and error paths
- [x] Builds AOT-clean with no warnings

---

### Issue 2: CLI Command — `twig delete`

**Goal:** Implement the full `twig delete <id>` command with all safety guards, confirmation, audit trail, and output formatting.

**Prerequisites:** Issue 1 (DeleteAsync API)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2172-4 | Create `DeleteCommand.cs` with full execution flow: resolve item, seed guard, fresh fetch with links, link guard (check all relation types + children), confirmation prompt (require "yes"), audit note on parent, delete, cache cleanup, telemetry, output | `src/Twig/Commands/DeleteCommand.cs` | M |
| T-2172-5 | Register `DeleteCommand` in DI, add `Delete` method to `TwigCommands` in `Program.cs`, add to `KnownCommands`, update `GroupedHelp.Show()` text, add `CommandExamples` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `src/Twig/Program.cs`, `src/Twig/CommandExamples.cs` | S |
| T-2172-6 | Add "delete" case to `HintEngine` with closing-recommendation hint | `src/Twig/Hints/HintEngine.cs` | S |
| T-2172-7 | Write comprehensive unit tests for `DeleteCommand`: seed guard, link guard (parent/child/related/predecessor/successor), children guard, confirmation accepted/declined, force flag, non-TTY guard, audit trail, cache cleanup, telemetry, output formats | `tests/Twig.Cli.Tests/Commands/DeleteCommandTests.cs` | M |

**Acceptance Criteria:**
- [x] `twig delete 1234` shows confirmation with item details and closing recommendation
- [x] Refuses deletion when links exist with descriptive error
- [x] `--force` bypasses confirmation
- [x] Seed items redirected to `twig seed discard`
- [x] Parent receives audit note on successful deletion
- [x] Local cache cleaned up after deletion
- [x] All tests pass, builds AOT-clean

---

### Issue 3: MCP Parity — `twig_delete` Tool

**Goal:** Add `twig_delete` MCP tool with two-phase confirmation and link safety checks.

**Prerequisites:** Issue 1 (DeleteAsync API)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2172-8 | Add `FormatDeleteConfirmation` and `FormatDeleted` methods to `McpResultBuilder` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S |
| T-2172-9 | Add `twig_delete` tool to `MutationTools` with two-phase flow: first call returns confirmation prompt with item details, second call with `confirmed: true` executes deletion. Link guard, seed guard, audit trail, cache cleanup. | `src/Twig.Mcp/Tools/MutationTools.cs` | M |
| T-2172-10 | Write MCP tool tests: confirmation flow, link guard, seed guard, confirmed deletion, cache cleanup, error paths | `tests/Twig.Mcp.Tests/Tools/MutationToolsDeleteTests.cs` | M |

**Acceptance Criteria:**
- [x] `twig_delete(id: 1234)` returns structured confirmation with `requiresConfirmation: true`
- [x] `twig_delete(id: 1234, confirmed: true)` executes deletion
- [x] Link guard blocks deletion with descriptive error
- [x] Seed items rejected
- [x] Tests cover all paths

---

## PR Groups

### PG-1: Domain + Infrastructure (DeleteAsync API)

**Type:** Deep  
**Tasks:** T-2172-1, T-2172-2, T-2172-3  
**Issues:** Issue 1  
**Estimated LoC:** ~150  
**Files:** ~3  
**Description:** Adds the `DeleteAsync` method to the domain interface and infrastructure REST client. Small, focused change with clear API contract.  
**Successors:** PG-2, PG-3 (both depend on this)

### PG-2: CLI Command + Tests

**Type:** Deep  
**Tasks:** T-2172-4, T-2172-5, T-2172-6, T-2172-7  
**Issues:** Issue 2  
**Estimated LoC:** ~700  
**Files:** ~8  
**Description:** Full CLI command implementation with all guards, confirmation, audit trail, DI registration, help text, hints, and comprehensive tests.  
**Predecessors:** PG-1

### PG-3: MCP Tool + Tests

**Type:** Deep  
**Tasks:** T-2172-8, T-2172-9, T-2172-10  
**Issues:** Issue 3  
**Estimated LoC:** ~400  
**Files:** ~3  
**Description:** MCP parity with two-phase confirmation flow and tests.  
**Predecessors:** PG-1

---

**Total Estimated LoC:** ~1,250  
**Total Files Affected:** ~14 (3 new, 11 modified)

---

## Execution Plan

### PR Group Summary

| Group | Name | Issues / Tasks | Dependencies | Type | Est. LoC |
|-------|------|----------------|--------------|------|----------|
| PG-1 | PG-1-domain-infrastructure | Issue 1 / T-2172-1, T-2172-2, T-2172-3 | None | Deep | ~150 |
| PG-2 | PG-2-cli-command | Issue 2 / T-2172-4, T-2172-5, T-2172-6, T-2172-7 | PG-1 | Deep | ~700 |
| PG-3 | PG-3-mcp-tool | Issue 3 / T-2172-8, T-2172-9, T-2172-10 | PG-1 | Deep | ~400 |

### Execution Order

1. **PG-1** (no dependencies) — Extend `IAdoWorkItemService` with `DeleteAsync` and implement it in `AdoRestClient`. This is the foundational API contract that both PG-2 and PG-3 depend on.
2. **PG-2** (depends on PG-1) — Full CLI command: `DeleteCommand.cs`, DI registration, `Program.cs` wiring, `HintEngine`, `CommandExamples`, and comprehensive unit tests. Can be developed in parallel with PG-3 once PG-1 merges.
3. **PG-3** (depends on PG-1, independent of PG-2) — MCP parity: `McpResultBuilder` formatters, `twig_delete` tool in `MutationTools`, and MCP unit tests. Can be developed in parallel with PG-2.

### Validation Strategy

**PG-1:**
- `dotnet build` must produce zero warnings (warnings-as-errors)
- Infrastructure unit tests: success (204), 404-idempotent, auth error, offline error
- AOT-compatibility: no reflection, no new unregistered serializable types

**PG-2:**
- All `DeleteCommandTests` pass: seed guard, link guard (all relation types + children), confirmation accepted/declined, `--force`, non-TTY guard, audit trail, cache cleanup, telemetry, output formats
- `GroupedHelpTests` completeness check (if exists) updated and passing
- `twig delete --help` renders correctly

**PG-3:**
- All `MutationToolsDeleteTests` pass: two-phase confirmation flow, link guard, seed guard, confirmed deletion, cache cleanup, error paths
- MCP schema validation passes (no unregistered JSON types)
- Full test suite (`dotnet test`) green before merge

---

## Completion

**Completed:** 2026-04-28  
**PRs Merged:** #139 (PG-1), #141 (PG-2), #145 (PG-3)  
**Version Tag:** *(see release tag)*

All 3 PR groups successfully implemented and merged. The delete command provides
guarded work item deletion with link safety checks across CLI (`twig delete`) and
MCP (`twig_delete`) interfaces. Comprehensive unit tests cover all safety guards,
confirmation flows, and error paths.
