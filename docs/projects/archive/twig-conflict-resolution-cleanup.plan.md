# Conflict Resolution Flow Extraction — Solution Design & Implementation Plan

**Revision**: 2 — Addresses technical review feedback (score 82/100)

---

## Executive Summary

Three CLI commands (`StateCommand`, `UpdateCommand`, `SaveCommand`) contain nearly identical conflict resolution orchestration code: fetch the remote work item, detect conflicts via `ConflictResolver.Resolve()`, branch on JSON vs. human output format, prompt the user for local/remote/abort, and apply the resolution. Additionally, `StateCommand` and `UpdateCommand` share an identical auto-push-notes pattern that pushes pending notes as ADO comments and clears them after a successful operation. This design extracts both patterns into dedicated helper classes in `Commands/` — `ConflictResolutionFlow` for the full resolve-prompt-apply cycle and `AutoPushNotesHelper` for the notes pattern — then refactors all three commands to delegate to these helpers. The result is ~90 fewer lines of duplicated logic, a single place to evolve conflict UX, and zero behavioral changes to existing tests or JSON output format.

---

## Background

### Current Architecture

Twig is an AOT-compiled .NET CLI tool (`PublishAot=true`, `IsAotCompatible=true`, .NET 10 SDK 10.0.104 per `global.json`) that manages Azure DevOps work items from the terminal. The relevant layers:

- **`Twig.Domain.Services.ConflictResolver`** — a pure static class that compares a local `WorkItem` and a remote `WorkItem` by revision, first-class properties, and Fields dictionary. Returns a discriminated union (`MergeResult.NoConflict | AutoMergeable | HasConflicts`).
- **`Twig.Commands.JsonConflictFormatter`** — an internal static class (already extracted) that formats `FieldConflict[]` as `{"conflicts":[...]}` JSON.
- **Three command classes** (`StateCommand`, `UpdateCommand`, `SaveCommand`) — each independently calls `ConflictResolver.Resolve()`, checks the result for `HasConflicts`, and duplicates the same prompt/apply/abort logic.

### Motivation

1. **Code duplication** — The conflict resolution orchestration is copy-pasted across three files (~30 lines each, ~90 total).
2. **Auto-push-notes duplication** — `StateCommand` (lines 131–143) and `UpdateCommand` (lines 88–100) have identical note-pushing loops.
3. **Maintenance risk** — Any change to the conflict UX, prompt text, exit codes, or JSON format must be replicated in three places.
4. **SaveCommand behavioral difference** — When the user chooses "remote" during conflicts, `SaveCommand` also calls `ClearChangesAsync()` (discarding all pending changes), whereas `StateCommand`/`UpdateCommand` do not. This must be accommodated in the design.

### Prior Art

`JsonConflictFormatter` was already extracted from the three commands to centralize JSON formatting (referenced as I-004 in code comments). This refactoring continues that trajectory.

---

## Problem Statement

1. **Conflict resolution orchestration** is duplicated across `StateCommand.cs` (lines 96–125, starting at the `ConflictResolver.Resolve()` call; line 93 is the `FetchAsync` call which remains in the command), `UpdateCommand.cs` (lines 52–81), and `SaveCommand.cs` (lines 53–82). All three implement the same sequence: detect → format-as-JSON-or-prompt → apply-local/remote/abort. Any UX change (e.g., adding a "merge" option, changing prompt text) requires synchronized edits in three files.

2. **Auto-push-notes logic** is duplicated between `StateCommand.cs` (lines 131–143) and `UpdateCommand.cs` (lines 88–100). Both iterate pending changes, filter for `"note"` type, call `AddCommentAsync`, and then `ClearChangesByTypeAsync`.

3. The duplication increases the risk of behavioral drift — one command could be updated while another is missed.

---

## Goals and Non-Goals

### Goals

- **G-1**: Extract the conflict resolution orchestration (detect → prompt → apply) into a single `ConflictResolutionFlow` helper in `Twig.Commands`.
- **G-2**: Extract the auto-push-notes pattern into an `AutoPushNotesHelper` helper in `Twig.Commands`.
- **G-3**: Refactor `StateCommand`, `UpdateCommand`, and `SaveCommand` to delegate to the helpers.
- **G-4**: Maintain 100% compatibility with all existing test assertions — no test modifications.
- **G-5**: Preserve the JSON conflict output format (`{"conflicts":[...]}`) and exit code behavior.
- **G-6**: Preserve AOT compatibility — no reflection, no `System.Text.Json` serializer attributes needed.

### Non-Goals

- **NG-1**: Changing the `ConflictResolver` domain service or `MergeResult` discriminated union.
- **NG-2**: Adding new conflict resolution strategies (e.g., field-level merge).
- **NG-3**: Changing the `JsonConflictFormatter` implementation.
- **NG-4**: Modifying test files — all existing assertions must pass without changes.
- **NG-5**: Changing the DI registration pattern or `Program.cs`.

---

## Requirements

### Functional Requirements

- **FR-001**: `ConflictResolutionFlow.ResolveAsync` MUST accept: a local `WorkItem`, a remote `WorkItem`, an `IOutputFormatter`, an output format string, an `IConsoleInput`, an `IWorkItemRepository`, a `string acceptRemoteMessage` for the success message on remote acceptance, and an optional `Func<Task>?` callback for "accept remote" side effects.
- **FR-002**: `ConflictResolutionFlow` MUST return a `ConflictOutcome` enum indicating one of: `Proceed` (no conflicts or keep-local), `AcceptedRemote`, `Aborted`, or `ConflictJsonEmitted`.
- **FR-003**: When `outputFormat` is `"json"` and conflicts exist, the helper MUST write JSON to stdout via `Console.WriteLine` and return `ConflictOutcome.ConflictJsonEmitted` (caller returns exit code 1).
- **FR-004**: When conflicts exist in non-JSON mode, the helper MUST print each conflict to stderr, prompt with "Keep [l]ocal, [r]emote, or [a]bort?", and handle the user's choice.
- **FR-005**: The "accept remote" path MUST invoke the optional `onAcceptRemote` callback first (if provided), then save the remote work item via `IWorkItemRepository.SaveAsync`, then print the caller-supplied `acceptRemoteMessage` via `fmt.FormatSuccess`. This ordering preserves SaveCommand's existing behavior where `ClearChangesAsync` is called before `SaveAsync`.
- **FR-006**: `AutoPushNotesHelper` MUST iterate pending changes, push notes via `IAdoWorkItemService.AddCommentAsync`, and clear note-type changes via `IPendingChangeStore.ClearChangesByTypeAsync`.
- **FR-007**: All three commands MUST produce identical outputs and side effects after refactoring.

### Non-Functional Requirements

- **NFR-001**: AOT compatibility — no reflection, no dynamic dispatch, no `JsonSerializer` usage. The helper MUST be a concrete sealed class (or static class with static methods).
- **NFR-002**: No new NuGet dependencies.
- **NFR-003**: Total new code (helpers, including doc comments) SHOULD be ≤ 120 lines; total removed code SHOULD be greater than 80 lines. (The helper code with standard XML doc comments totals ~115 lines; the net reduction is ~90 lines across the three commands minus ~115 lines of new helper code, yielding fewer total lines and a single-point-of-change.)
- **NFR-004**: `CancellationToken` is intentionally omitted from helper method signatures, consistent with the existing command code style — all commands in this project omit `CancellationToken` on interface calls (the underlying interfaces accept optional `CancellationToken` parameters with defaults).

---

## Proposed Design

### Architecture Overview

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│StateCommand  │    │UpdateCommand │    │SaveCommand   │
│              │    │              │    │              │
│  delegates   │    │  delegates   │    │  delegates   │
│      ↓       │    │      ↓       │    │      ↓       │
├──────────────┤    ├──────────────┤    ├──────────────┤
│ ConflictRes. │    │ ConflictRes. │    │ ConflictRes. │
│ Flow.Resolve │    │ Flow.Resolve │    │ Flow.Resolve │
│   Async()    │    │   Async()    │    │   Async()    │
└──────┬───────┘    └──────┬───────┘    └──────┬───────┘
       │                   │                   │
       └───────────┬───────┘                   │
                   ↓                           ↓
          AutoPushNotesHelper          (SaveCommand has its
          .PushAndClearAsync()          own push logic — does
                                        not use notes helper)
```

Both helpers live in `Twig.Commands` (CLI layer) because they orchestrate console I/O and format output — not domain logic.

### Key Components

#### 1. `ConflictResolutionFlow` (new static class)

**File**: `src/Twig/Commands/ConflictResolutionFlow.cs`

**Responsibilities**:
- Accept local/remote `WorkItem`, formatter, output format, console input, work item repository, a caller-supplied success message for the "accept remote" path, and an optional async callback for "accept remote" side effects.
- Call `ConflictResolver.Resolve(local, remote)`.
- If `NoConflict` or `AutoMergeable`, return `ConflictOutcome.Proceed`.
- If `HasConflicts` and JSON format, write JSON to stdout and return `ConflictOutcome.ConflictJsonEmitted`.
- If `HasConflicts` and human format, display conflicts, prompt user, handle l/r/a:
  - `l` → return `ConflictOutcome.Proceed` (caller proceeds with local).
  - `r` → invoke optional callback, save remote, print caller-supplied `acceptRemoteMessage`, return `ConflictOutcome.AcceptedRemote`.
  - `a` / null → print "Aborted.", return `ConflictOutcome.Aborted`.

**Accept-remote message handling**: Each command passes its own message string to `acceptRemoteMessage`:
- **StateCommand**: `$"#{item.Id} updated from remote."`
- **UpdateCommand**: `$"#{local.Id} updated from remote."`
- **SaveCommand**: `$"#{item.Id} synced from remote. Pending changes discarded."`

This avoids the helper needing to compose different messages and keeps the exact output strings preserved for test compatibility.

```csharp
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>Outcome of the conflict resolution flow.</summary>
internal enum ConflictOutcome
{
    /// <summary>No conflicts, auto-mergeable, or user chose to keep local. Caller should proceed.</summary>
    Proceed,
    /// <summary>User chose to accept remote. Cache already updated. Caller should return 0.</summary>
    AcceptedRemote,
    /// <summary>User chose to abort. Caller should return 0.</summary>
    Aborted,
    /// <summary>JSON conflict output was emitted. Caller should return 1.</summary>
    ConflictJsonEmitted,
}

/// <summary>
/// Encapsulates the CLI-layer conflict resolution orchestration shared by
/// StateCommand, UpdateCommand, and SaveCommand.
/// </summary>
internal static class ConflictResolutionFlow
{
    /// <summary>
    /// Detects conflicts between <paramref name="local"/> and <paramref name="remote"/>,
    /// prompts the user if needed, and applies the resolution.
    /// </summary>
    internal static async Task<ConflictOutcome> ResolveAsync(
        WorkItem local,
        WorkItem remote,
        IOutputFormatter fmt,
        string outputFormat,
        IConsoleInput consoleInput,
        IWorkItemRepository workItemRepo,
        string acceptRemoteMessage,
        Func<Task>? onAcceptRemote = null)
    {
        var mergeResult = ConflictResolver.Resolve(local, remote);
        if (mergeResult is not MergeResult.HasConflicts conflicts)
            return ConflictOutcome.Proceed;

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                JsonConflictFormatter.FormatConflictsAsJson(conflicts.ConflictingFields));
            return ConflictOutcome.ConflictJsonEmitted;
        }

        foreach (var c in conflicts.ConflictingFields)
            Console.Error.WriteLine(
                fmt.FormatError($"Conflict on '{c.FieldName}': local='{c.LocalValue}', remote='{c.RemoteValue}'"));

        Console.Write("Keep [l]ocal, [r]emote, or [a]bort? ");
        var choice = consoleInput.ReadLine()?.Trim().ToLowerInvariant();

        if (choice == "a" || choice is null)
        {
            Console.WriteLine(fmt.FormatInfo("Aborted."));
            return ConflictOutcome.Aborted;
        }

        if (choice == "r")
        {
            if (onAcceptRemote is not null)
                await onAcceptRemote();
            await workItemRepo.SaveAsync(remote);
            Console.WriteLine(fmt.FormatSuccess(acceptRemoteMessage));
            return ConflictOutcome.AcceptedRemote;
        }

        // 'l' or any unrecognized input: proceed with local changes
        return ConflictOutcome.Proceed;
    }
}
```

#### 2. `AutoPushNotesHelper` (new static class)

**File**: `src/Twig/Commands/AutoPushNotesHelper.cs`

**Responsibilities**:
- Accept a work item ID, `IPendingChangeStore`, and `IAdoWorkItemService`.
- Iterate pending changes, find `"note"` types, push via `AddCommentAsync`.
- If any notes were pushed, call `ClearChangesByTypeAsync(id, "note")`.

```csharp
using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Pushes pending notes as ADO comments and clears them.
/// Shared by StateCommand and UpdateCommand.
/// </summary>
internal static class AutoPushNotesHelper
{
    /// <summary>
    /// Pushes any pending notes for the given work item as ADO comments,
    /// then clears the note-type pending changes.
    /// </summary>
    internal static async Task PushAndClearAsync(
        int workItemId,
        IPendingChangeStore pendingChangeStore,
        IAdoWorkItemService adoService)
    {
        var pendingChanges = await pendingChangeStore.GetChangesAsync(workItemId);
        var hasNotes = false;

        foreach (var change in pendingChanges)
        {
            if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase)
                && change.NewValue is not null)
            {
                await adoService.AddCommentAsync(workItemId, change.NewValue);
                hasNotes = true;
            }
        }

        if (hasNotes)
            await pendingChangeStore.ClearChangesByTypeAsync(workItemId, "note");
    }
}
```

### Data Flow

**Conflict resolution flow (all three commands)**:

```
Command.ExecuteAsync()
  │
  ├── Fetch remote via adoService.FetchAsync(id)
  │
  ├── ConflictResolutionFlow.ResolveAsync(local, remote, fmt, format, input, repo, msg, callback?)
  │     │
  │     ├── ConflictResolver.Resolve(local, remote)  ← domain service (unchanged)
  │     │
  │     ├── [HasConflicts + JSON] → stdout JSON, return ConflictJsonEmitted
  │     ├── [HasConflicts + human] → stderr errors, prompt l/r/a
  │     │     ├── 'l' → return Proceed
  │     │     ├── 'r' → callback?(), repo.SaveAsync(remote), print msg, return AcceptedRemote
  │     │     └── 'a' → return Aborted
  │     └── [NoConflict] → return Proceed
  │
  ├── Handle outcome:
  │     ├── ConflictJsonEmitted → return 1
  │     ├── AcceptedRemote → return 0
  │     ├── Aborted → return 0
  │     └── Proceed → continue with command-specific logic
  │
  └── (command-specific: PatchAsync, auto-push notes, update cache, etc.)
```

**Auto-push notes flow (StateCommand and UpdateCommand only)**:

```
Command.ExecuteAsync()  [after successful PatchAsync]
  │
  └── AutoPushNotesHelper.PushAndClearAsync(id, pendingStore, adoService)
        │
        ├── pendingStore.GetChangesAsync(id)
        ├── For each "note" change → adoService.AddCommentAsync(id, text)
        └── If any notes → pendingStore.ClearChangesByTypeAsync(id, "note")
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Static classes, not instance services** | Avoids DI registration changes in `Program.cs`. The helpers are pure orchestration with no state. Matches `JsonConflictFormatter` precedent. AOT-safe. |
| **`ConflictOutcome` enum return type** | Provides a clear, exhaustive set of outcomes the caller can switch on. Simpler than a `Result<T>` type and avoids over-engineering. |
| **`Func<Task>?` callback for SaveCommand** | SaveCommand needs `ClearChangesAsync` called on "accept remote". A callback avoids the helper knowing about `IPendingChangeStore.ClearChangesAsync` directly (which is SaveCommand-specific behavior, not shared). |
| **`string acceptRemoteMessage` as required parameter** | The three commands produce different success messages on remote acceptance: StateCommand/UpdateCommand use `"#{id} updated from remote."` while SaveCommand uses `"#{id} synced from remote. Pending changes discarded."`. Hardcoding any single message would break at least one command's test assertions. Making this caller-supplied preserves exact output. |
| **Separate `AutoPushNotesHelper`** | The notes pattern is independent from conflict resolution. Keeping them separate follows single-responsibility and allows `SaveCommand` (which does NOT use auto-push-notes) to remain unaffected. |
| **`internal` visibility** | Helpers are CLI-layer implementation details. `InternalsVisibleTo("Twig.Cli.Tests")` already exists (line 27 of `Twig.csproj`), so tests can verify if needed. |
| **No `CancellationToken` on helper signatures** | Consistent with all existing command code in this project — no command passes `CancellationToken` to interface methods. The underlying interfaces define optional `CancellationToken` parameters with defaults. Adding tokens would be correct in theory but inconsistent with the project's conventions. |
| **`onAcceptRemote` invoked before `SaveAsync`** | In `SaveCommand`, `ClearChangesAsync` is called before `SaveAsync(remote)` (lines 76–77). The callback-then-save ordering in the helper preserves this sequence. For `StateCommand`/`UpdateCommand` the callback is `null`, so ordering is irrelevant — only `SaveAsync` executes. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Instance class with DI** | Standard .NET pattern; injectable/mockable | Requires `Program.cs` registration; adds constructor parameters to commands; over-engineered for stateless helpers | Rejected — unnecessary complexity |
| **Base class / inheritance** | Commands could inherit shared behavior | Commands already use primary constructors; C# sealed classes; inheritance is rigid | Rejected — composition over inheritance |
| **Extension methods on `WorkItem`** | Fluent API | Conflict resolution involves I/O (Console, IConsoleInput) which is inappropriate on a domain object extension | Rejected — wrong layer |
| **Move to Domain layer** | Centralized | Console I/O is a CLI concern; domain should not depend on formatters or console input | Rejected — violates layering |
| **Single helper class combining both patterns** | Fewer files | Violates SRP; auto-push-notes has no relation to conflict resolution | Rejected — keep responsibilities separate |

---

## Dependencies

- **Internal**: `ConflictResolver` (Domain), `JsonConflictFormatter` (Commands), `IOutputFormatter` (Formatters), `IConsoleInput` (Domain/Interfaces), `IWorkItemRepository` (Domain/Interfaces), `IAdoWorkItemService` (Domain/Interfaces), `IPendingChangeStore` (Domain/Interfaces).
- **External**: None — no new NuGet packages.
- **Sequencing**: No prerequisites. The refactoring is purely internal.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `StateCommand.cs` | Replace ~30 lines (conflict block + notes block) with 2 helper calls |
| `UpdateCommand.cs` | Replace ~30 lines (conflict block + notes block) with 2 helper calls |
| `SaveCommand.cs` | Replace ~30 lines (conflict block) with 1 helper call |
| `ConflictResolutionFlow.cs` | New file (~80 lines including doc comments) |
| `AutoPushNotesHelper.cs` | New file (~35 lines including doc comments) |

### Backward Compatibility

- **100% backward compatible** — all public APIs, exit codes, console outputs, and JSON formats remain unchanged.
- Test files are NOT modified.

### Performance

- No performance impact — the same operations execute in the same order. The only change is call indirection through static methods (zero-cost in AOT).

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Subtle behavioral drift in message formatting between commands and helper | Low | Medium | Each command passes its own `acceptRemoteMessage` string — exact message preservation verified by existing tests |
| Missing a behavioral difference between the three commands' conflict handling | Low | High | Side-by-side diff of all three implementations performed during research; only SaveCommand's `ClearChangesAsync` and its different success message differ, both handled via callback and `acceptRemoteMessage` parameter |
| AOT trimming removes the static helper | Very Low | High | Static methods called directly are never trimmed; no reflection involved |

---

## Open Questions

None — the design is fully grounded in the existing codebase and all behavioral differences have been identified and accounted for.

---

## Implementation Phases

### Phase 1: Create Helpers (Epic 1)
**Exit criteria**: Both helper files compile, existing tests pass (no command changes yet).

### Phase 2: Refactor Commands (Epic 2)
**Exit criteria**: All three commands delegate to helpers, all existing tests pass with zero modifications.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/ConflictResolutionFlow.cs` | CLI-layer conflict resolution orchestration helper |
| `src/Twig/Commands/AutoPushNotesHelper.cs` | Auto-push pending notes helper |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/StateCommand.cs` | Replace conflict resolution block (lines 96–125, starting at `ConflictResolver.Resolve()` call) with `ConflictResolutionFlow.ResolveAsync()` call; replace auto-push-notes block (lines 131–143) with `AutoPushNotesHelper.PushAndClearAsync()` call. Line 93 (`FetchAsync`) remains in StateCommand since the helper receives `remote` as a parameter. |
| `src/Twig/Commands/UpdateCommand.cs` | Replace conflict resolution block (lines 52–81) with `ConflictResolutionFlow.ResolveAsync()` call; replace auto-push-notes block (lines 88–100) with `AutoPushNotesHelper.PushAndClearAsync()` call |
| `src/Twig/Commands/SaveCommand.cs` | Replace conflict resolution block (lines 53–82) with `ConflictResolutionFlow.ResolveAsync()` call, passing `ClearChangesAsync` callback and SaveCommand-specific success message |

### Deleted Files

None.

---

## Implementation Plan

### EPIC-001: Create Helper Classes

**Goal**: Implement `ConflictResolutionFlow` and `AutoPushNotesHelper` as new static helper classes in `Twig.Commands`.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `ConflictResolutionFlow.cs` with `ConflictOutcome` enum and `ResolveAsync` static method (8 parameters: `local`, `remote`, `fmt`, `outputFormat`, `consoleInput`, `workItemRepo`, `acceptRemoteMessage`, `onAcceptRemote?`). Logic: call `ConflictResolver.Resolve()`, handle JSON output via `JsonConflictFormatter`, prompt user via `IConsoleInput`, invoke optional callback then save remote via `IWorkItemRepository.SaveAsync`, print caller-supplied `acceptRemoteMessage`. | `src/Twig/Commands/ConflictResolutionFlow.cs` | DONE |
| ITEM-002 | IMPL | Create `AutoPushNotesHelper.cs` with `PushAndClearAsync` static method. Logic: get pending changes, filter for "note" type, call `AddCommentAsync` for each, call `ClearChangesByTypeAsync` if any notes were pushed. | `src/Twig/Commands/AutoPushNotesHelper.cs` | DONE |
| ITEM-003 | TEST | Build the solution and run all existing tests to confirm the new files compile and no regressions are introduced (helpers are not yet called). | — | DONE |

**Acceptance Criteria**:
- [x] `ConflictResolutionFlow.cs` compiles and contains `ConflictOutcome` enum + `ResolveAsync` method with 8-parameter signature including `acceptRemoteMessage`
- [x] `AutoPushNotesHelper.cs` compiles and contains `PushAndClearAsync` method
- [x] `dotnet build` succeeds with no warnings from new files
- [x] All existing tests pass

---

### EPIC-002: Refactor Commands to Use Helpers

**Goal**: Replace duplicated conflict resolution and auto-push-notes code in `StateCommand`, `UpdateCommand`, and `SaveCommand` with calls to the new helpers.

**Prerequisites**: EPIC-001 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-004 | IMPL | Refactor `StateCommand.ExecuteAsync()`: replace lines 96–125 (conflict detection/prompt/apply block, starting at `ConflictResolver.Resolve()`) with a call to `ConflictResolutionFlow.ResolveAsync(item, remote, fmt, outputFormat, consoleInput, workItemRepo, $"#{item.Id} updated from remote.")`. Line 93 (`var remote = await adoService.FetchAsync(item.Id)`) remains in StateCommand. Handle `ConflictOutcome`: `ConflictJsonEmitted` → return 1, `AcceptedRemote` or `Aborted` → return 0, `Proceed` → continue. Replace lines 131–143 (auto-push notes) with `AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService)`. | `src/Twig/Commands/StateCommand.cs` | DONE |
| ITEM-005 | IMPL | Refactor `UpdateCommand.ExecuteAsync()`: replace lines 52–81 (conflict block) with `ConflictResolutionFlow.ResolveAsync(local, remote, fmt, outputFormat, consoleInput, workItemRepo, $"#{local.Id} updated from remote.")`. Handle outcomes same as StateCommand. Replace lines 88–100 (auto-push notes) with `AutoPushNotesHelper.PushAndClearAsync(local.Id, pendingChangeStore, adoService)`. | `src/Twig/Commands/UpdateCommand.cs` | DONE |
| ITEM-006 | IMPL | Refactor `SaveCommand.ExecuteAsync()`: replace lines 53–82 (conflict block) with `ConflictResolutionFlow.ResolveAsync(item, remote, fmt, outputFormat, consoleInput, workItemRepo, $"#{item.Id} synced from remote. Pending changes discarded.", onAcceptRemote: () => pendingChangeStore.ClearChangesAsync(item.Id))`. Handle outcomes same as other commands. | `src/Twig/Commands/SaveCommand.cs` | DONE |
| ITEM-007 | TEST | Run the full test suite: `StateCommandTests`, `UpdateCommandTests`, `EditSaveCommandTests`, `ConflictUxTests`, and all other test classes. Verify zero test modifications and zero failures. | — | DONE |

**Acceptance Criteria**:
- [x] `StateCommand.cs` conflict block replaced with `ConflictResolutionFlow.ResolveAsync()` call
- [x] `StateCommand.cs` auto-push-notes block replaced with `AutoPushNotesHelper.PushAndClearAsync()` call
- [x] `UpdateCommand.cs` conflict block replaced with `ConflictResolutionFlow.ResolveAsync()` call
- [x] `UpdateCommand.cs` auto-push-notes block replaced with `AutoPushNotesHelper.PushAndClearAsync()` call
- [x] `SaveCommand.cs` conflict block replaced with `ConflictResolutionFlow.ResolveAsync()` call with `ClearChangesAsync` callback and `"#{item.Id} synced from remote. Pending changes discarded."` message
- [x] All 4 test files pass with zero modifications: `StateCommandTests.cs`, `UpdateCommandTests.cs`, `EditSaveCommandTests.cs`, `ConflictUxTests.cs`
- [x] Full `dotnet test` passes
- [x] JSON conflict output format unchanged (verified by `Save_Conflict_JsonOutput_ReturnsConflictsAndExitOne` test)

---

## References

- `src/Twig/Commands/StateCommand.cs` — current conflict resolution (lines 96–125) and auto-push-notes (lines 131–143)
- `src/Twig/Commands/UpdateCommand.cs` — current conflict resolution (lines 52–81) and auto-push-notes (lines 88–100)
- `src/Twig/Commands/SaveCommand.cs` — current conflict resolution (lines 53–82)
- `src/Twig.Domain/Services/ConflictResolver.cs` — domain-level conflict detection (unchanged)
- `src/Twig/Commands/JsonConflictFormatter.cs` — JSON formatting helper (unchanged, precedent for this extraction)
- `tests/Twig.Cli.Tests/Commands/ConflictUxTests.cs` — conflict UX test coverage
- `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` — state command tests including auto-push-notes
- `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` — update command tests including auto-push-notes
- `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` — save command tests
- `global.json` — .NET 10 SDK 10.0.104 (not .NET 9 as stated in original request)
