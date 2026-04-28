# Context Commands Realignment

> **Epic:** #2149 — Context Commands Realignment — Remove Status, Slim Set, Enhance Show
> **Status**: 🔨 In Progress
> **Revision:** 0 (Initial draft)
> **Spec:** [docs/specs/context-commands.spec.md](../specs/context-commands.spec.md)

---

## Executive Summary

The context command surface (`set`, `show`, `status`) has accumulated overlapping responsibilities: `set` renders a full dashboard identical to `status`, `status` and `show` both display work items with subtly different sync/enrichment behaviors, and users cannot predict which command to use. This plan realigns the surface per the functional spec: **delete `status` entirely** (CLI command + MCP tool), **slim `set` to a context-switch-only command** (resolve → update pointer → emit confirmation line), and **enhance `show` to absorb all display responsibilities** (no-args mode for active item, pending changes, git context, branch detection hints, sync-first for machine output, `--no-refresh` for cache-only). The result is a clean separation: `set` mutates context, `show` displays it.

---

## Background

### Current State

Three commands share overlapping display responsibilities:

| Command | Context Mutation | Dashboard Rendering | Sync | Pending Changes | Git Context | Hints |
|---------|-----------------|-------------------|------|-----------------|-------------|-------|
| `set` | ✅ Sets active item, records history, writes prompt | ✅ Full Spectre dashboard with children, parent, links, progress, status fields | ✅ Targeted sync (item + parents) | ✅ Displays counts | ❌ | ✅ |
| `status` | ❌ | ✅ Full Spectre dashboard (identical to `set` output) | ✅ Working set sync | ✅ Displays counts | ❌ | ✅ |
| `show` | ❌ | ✅ Full Spectre dashboard (no pending changes) | ✅ Item-only sync | ❌ | ❌ | ❌ |

Key problems:
1. **`set` does too much** — 239 lines including dashboard rendering, child/parent loading, working set computation, targeted sync, and hint engine output. The spec says `set` should be mutation-only.
2. **`status` and `show` overlap** — Both render the same Spectre dashboard via `RenderStatusAsync`. Users cannot predict which to use.
3. **`show` requires an ID** — No way to inspect the active item via `show`; users must use `status` instead.
4. **Machine output inconsistency** — `show` doesn't sync before JSON output; `status` does.
5. **Smart landing routes to `status`** — `twig` with no args routes to `status` (Program.cs line 118), which must be redirected.

### Architecture

All three commands follow the same pattern:
- Constructor with primary DI injection via `CommandContext` + domain services
- `ExecuteAsync` → `ExecuteCoreAsync` with telemetry wrapper via `TelemetryHelper.TrackCommand`
- Rendering via `ctx.Resolve(outputFormat)` → `(IOutputFormatter, IAsyncRenderer?)` tuple
- TTY path: `SpectreRenderer.RenderStatusAsync` / `RenderWithSyncAsync` (two-pass: cached → sync → revised)
- Non-TTY path: `HumanOutputFormatter.FormatWorkItem` / `JsonOutputFormatter.FormatWorkItem` / `MinimalOutputFormatter.FormatWorkItem`

### Call-Site Audit

Cross-cutting symbols affected by this change:

| Symbol | File | Current Usage | Impact |
|--------|------|---------------|--------|
| `StatusCommand` (class) | `Commands/StatusCommand.cs` | 245 lines, full command | **Delete** |
| `StatusCommand` (DI) | `DependencyInjection/CommandRegistrationModule.cs:44` | `AddSingleton<StatusCommand>()` | **Remove line** |
| `StatusCommand` (CLI) | `Program.cs:368-369` | `Status()` method → `StatusCommand.ExecuteAsync()` | **Delete method** |
| `StatusCommand` (help) | `Program.cs:926` | `"status"` in `KnownCommands` | **Remove entry** |
| `StatusCommand` (help) | `Program.cs:1049` | `"status"` in grouped help text | **Remove line** |
| `StatusCommand` (smart landing) | `Program.cs:118` | `args = ["status"]` | **Change to `["show"]`** |
| `twig_status` (MCP) | `Mcp/Tools/ContextTools.cs:92-118` | `Status()` MCP tool method | **Delete method** |
| `StatusResult` (domain) | `Domain/Services/Workspace/StatusResult.cs` | Union type for status queries | **Evaluate: keep if MCP show uses it, else delete** |
| `McpResultBuilder.FormatStatus` | `Mcp/Services/McpResultBuilder.cs:35-85` | Formats StatusResult to JSON | **Evaluate: delete if StatusResult deleted** |
| `RenderStatusAsync` | `Rendering/IAsyncRenderer.cs:35-45` | Renderer method used by all 3 commands | **Keep** (ShowCommand still uses it) |
| `BuildStatusViewAsync` | `Rendering/SpectreRenderer.cs` | Builds Spectre renderable for status view | **Keep** (ShowCommand uses it) |
| `HintEngine.GetHints("status")` | `Hints/HintEngine.cs:128-134` | Stale seed hints for status command | **Migrate to `"show"` key** |
| `HintEngine.GetHints("set")` | `Hints/HintEngine.cs:60-63` | "Try: twig status" hint text | **Update hint text** |
| `FormatStatusSummary` | `Formatters/IOutputFormatter.cs:40` | One-line summary for status sync path | **Keep** (ShowCommand may use it) |
| `ContextTools` (MCP registration) | `Mcp/Program.cs:73` | `.WithTools<ContextTools>()` | **Keep** (still has `twig_set`) |
| `ContextToolsStatusTests` | `Tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | 255 lines of status MCP tests | **Delete file** |
| `StatusCommandTests` | `Tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | CLI status tests | **Delete file** |
| `StatusCommand_CacheAwareTests` | `Tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Cache behavior tests | **Delete file** |

---

## Problem Statement

The context command surface violates separation of concerns. `set` is supposed to be a pointer-mutation command but renders a full dashboard (140+ lines of rendering code), loads children/parents/links, computes working sets, runs targeted syncs, and outputs hints — making it slow and unpredictable. Users who want to check the active item must choose between `status` (active item + pending changes) and `show` (requires explicit ID, no pending changes), with no clear guidance on which to use. Machine consumers face inconsistent behavior: `show --output json` returns cache-only data while `status --output json` syncs first. The `--no-refresh` flag exists on both commands with subtly different semantics.

---

## Goals and Non-Goals

### Goals

1. **G-1:** `twig set` completes in <100ms for cache hits by removing all rendering, sync, and enrichment
2. **G-2:** `twig show` (no args) replaces `twig status` as the single read-only display command
3. **G-3:** Machine output formats (`json`, `jsonc`, `minimal`) sync synchronously before emitting, producing complete data in a single pass
4. **G-4:** `--no-refresh` provides consistent cache-only behavior across all output formats
5. **G-5:** `twig status` is fully removed — no command, no alias, no MCP tool
6. **G-6:** Pending changes and git context are visible in `twig show` output
7. **G-7:** Branch detection hints guide users when no active item is set

### Non-Goals

1. **NG-1:** Adding new MCP tools beyond replacing `twig_status` with enhanced `twig_show` — the existing `twig_show` in `NavigationTools.cs` already exists and will be enhanced
2. **NG-2:** Changing the `show-batch` command behavior (already cache-only, spec-compliant)
3. **NG-3:** Adding deprecation aliases or transition periods for `status` — this is a clean break per spec
4. **NG-4:** Modifying the Spectre rendering infrastructure (`SpectreRenderer`, `IAsyncRenderer`) — we reuse existing `RenderStatusAsync`/`BuildStatusViewAsync`
5. **NG-5:** Changing the `twig_set` MCP tool — it already outputs minimal JSON without dashboard rendering

---

## Requirements

### Functional

| ID | Requirement | Source |
|----|-------------|--------|
| FR-1 | `twig set <id>` resolves item, updates context, records history, writes prompt state, outputs confirmation line | Spec §set |
| FR-2 | `twig set` outputs: human `Set active item: #42 Title [State]`, JSON `{"id":42,"title":"...","state":"...","type":"..."}`, minimal `#42` | Spec §set |
| FR-3 | `twig set` does NOT load children, parents, links, field definitions, or compute child progress | Spec §set |
| FR-4 | `twig set` does NOT run working set computation, targeted sync, or hint engine | Spec §set |
| FR-5 | `twig show` (no args) resolves the active item from context store | Spec §show |
| FR-6 | `twig show` (no args) with no active item shows branch detection hint + exit 1 | Spec §show |
| FR-7 | `twig show` displays pending changes (field count + note count) | Spec §show |
| FR-8 | `twig show` displays git context (current branch + linked PRs) | Spec §show |
| FR-9 | `twig show --output json` syncs synchronously before emitting JSON | Spec §show |
| FR-10 | `twig show --no-refresh` skips sync for all output formats | Spec §show |
| FR-11 | `twig status` is removed from CLI (command, help, known commands) | Spec §status-removed |
| FR-12 | `twig_status` MCP tool is removed | Spec §status-removed |
| FR-13 | Smart landing (`twig` with no args) routes to `show` instead of `status` | Implied |
| FR-14 | `twig_show` MCP tool enhanced with pending changes in response | Implied |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | `twig set` cache-hit path completes in <100ms (no network calls) |
| NFR-2 | All JSON output uses source-generated `TwigJsonContext` (AOT-safe) |
| NFR-3 | No new warnings (TreatWarningsAsErrors) |
| NFR-4 | Existing test coverage migrated — no net loss of behavioral coverage |

---

## Proposed Design

### Architecture Overview

```
BEFORE:                              AFTER:
┌──────────┐                         ┌──────────┐
│ twig set │ ──resolve──render──sync  │ twig set │ ──resolve──confirm
└──────────┘                         └──────────┘
┌────────────┐                       ┌───────────┐
│twig status │ ──resolve──render──sync│ twig show │ ──resolve──render──sync
└────────────┘                       │ (no args) │   +pending +git
┌───────────┐                        └───────────┘
│ twig show │ ──lookup──render──sync  ┌───────────┐
│ (by ID)   │                        │ twig show │ ──lookup──render──sync
└───────────┘                        │ (by ID)   │   +pending +git
                                     └───────────┘
```

### Key Components

#### 1. Slimmed SetCommand (~80 lines, down from 239)

Remove lines 117-235 (parent chain hydration, dashboard rendering, working set extension, targeted sync, hint engine). Keep:
- Input validation (empty → exit 2)
- Item resolution by ID (cache → ADO fallback) or pattern (cache-only, disambiguation)
- `contextStore.SetActiveWorkItemIdAsync(item.Id)`
- `historyStore.RecordVisitAsync(item.Id)`
- `promptStateWriter.WritePromptStateAsync()`
- Minimal confirmation output (new `FormatSetConfirmation` method on formatters)

Constructor shrinks from 13 parameters to 7:
```csharp
public sealed class SetCommand(
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    IPromptStateWriter? promptStateWriter = null,
    INavigationHistoryStore? historyStore = null)
```

Removed dependencies: `SyncCoordinatorFactory`, `WorkingSetService`, `StatusFieldConfigReader`, `IPendingChangeStore`, `IFieldDefinitionStore`, `IProcessConfigurationProvider`, `ContextChangeService`.

#### 2. Enhanced ShowCommand (~350 lines, up from 207)

The `id` parameter becomes optional (`int? id = null`). New behavior branches:

**No-args path (replaces `status`):**
1. Read active item ID from `IContextStore`
2. No active item → branch detection hint → exit 1
3. Active item not in cache → auto-fetch via `ActiveItemResolver` (G-3 contract)
4. Fetch fails → exit 1
5. Enrichment: children, parent, links, field defs, status fields, pending changes, git context
6. Render with sync (TTY human) or sync-first (machine formats)

**With-ID path (existing, enhanced):**
1. Cache-first lookup (existing behavior)
2. Same enrichment as no-args path (adds pending changes + git context)
3. Does NOT change active context
4. Does NOT record navigation history

New dependencies added to constructor:
- `IContextStore` (for no-args active item resolution)
- `ActiveItemResolver` (for auto-fetch on cache miss)
- `IPendingChangeStore` (for pending changes display)
- `IAdoGitService?` (for linked PR lookup)
- `WorkingSetService` (for working set sync in no-args path)

**Sync behavior per spec:**
- Machine formats (json, jsonc, minimal) + non-TTY human: sync synchronously → output
- TTY human: render cached → background sync → revise display
- `--no-refresh`: skip sync for all formats

#### 3. Formatter Changes

Add `FormatSetConfirmation` to `IOutputFormatter`:
```csharp
string FormatSetConfirmation(WorkItem item);
```
- Human: `Set active item: #42 Fix login bug [Active]`
- JSON: `{"id":42,"title":"Fix login bug","state":"Active","type":"Task"}`
- Minimal: `#42`

Add pending changes and git context to JSON work item output when rendering via ShowCommand.

#### 4. MCP Tool Changes

**Delete `twig_status`** from `ContextTools.cs` (lines 92-118).

**Enhance `twig_show`** in `NavigationTools.cs`:
- Add optional `bool includeContext = false` parameter (or always include pending changes)
- Add pending changes to response when item is the active item
- Keep the existing cache-first → ADO fallback pattern

#### 5. HintEngine Updates

- Rename `"status"` case to `"show"` in `GetHints` switch
- Update `"set"` hint from `"Try: twig status, twig tree, twig state <name>"` to `"Try: twig show, twig tree, twig state <name>"`

#### 6. Smart Landing Update

Change `Program.cs` line 118 from `args = ["status"]` to `args = ["show"]`.

### Data Flow — `twig set 42`

```
1. Parse "42" → int
2. ActiveItemResolver.ResolveByIdAsync(42) → Found/FetchedFromAdo/Unreachable
3. contextStore.SetActiveWorkItemIdAsync(42)
4. historyStore.RecordVisitAsync(42)
5. promptStateWriter.WritePromptStateAsync()
6. Console.WriteLine("Set active item: #42 Fix login bug [Active]")
7. return 0
```

### Data Flow — `twig show` (no args, TTY)

```
1. contextStore.GetActiveWorkItemIdAsync() → 42
2. activeItemResolver.GetActiveItemAsync() → WorkItem
3. Load children, parent, links, field defs, status fields
4. Load pending changes (field count + note count)
5. Detect git branch, query linked PRs
6. SpectreRenderer.RenderWithSyncAsync(
     buildCachedView → render dashboard with all data,
     performSync → SyncWorkingSetAsync(workingSet),
     buildRevisedView → rebuild with fresh data)
7. Output hints (stale data, seeds)
8. return 0
```

### Data Flow — `twig show --output json` (no args)

```
1. contextStore.GetActiveWorkItemIdAsync() → 42
2. activeItemResolver.GetActiveItemAsync() → WorkItem
3. SyncWorkingSetAsync(workingSet) ← synchronous, blocks
4. Reload item + children + parent + links from cache (fresh after sync)
5. Load pending changes + git context
6. Console.WriteLine(jsonFmt.FormatWorkItem(item, ...))
7. return 0
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | No deprecation alias for `status` | Spec explicitly calls for clean break. Users get clear error on unknown command via `GroupedHelp.ShowUnknown`. |
| DD-2 | `set` keeps `ContextChangeService.ExtendWorkingSetAsync` call | This additively hydrates the cache for follow-up `show` calls. Fast, fire-and-forget, no user-visible output. Removed from spec but worth keeping for cache warmth. |
| DD-3 | `show` no-args path syncs working set (like old `status`), not just target item | Active item display benefits from fresh sibling/parent data for progress computation. |
| DD-4 | Git context is best-effort | `IAdoGitService?` is nullable. Branch detection uses `GitBranchReader` (filesystem). PR lookup can fail silently. |
| DD-5 | `StatusResult` domain type is kept | MCP `twig_show` enhancement may reuse parts. Deletion deferred to cleanup. |

---

## Alternatives Considered

### A1: Deprecation alias for `status`

**Option:** Add a hidden `status` alias that forwards to `show` with a deprecation warning, remove after 2 releases.

**Pros:** Smoother migration for users with muscle memory.
**Cons:** Increases maintenance surface, delays cleanup, spec explicitly says no transition period.
**Decision:** Rejected per spec — clean break.

### A2: Keep `set` rendering but make it optional

**Option:** Add `--quiet` flag to `set` instead of removing dashboard.

**Pros:** Backward-compatible.
**Cons:** Violates separation of concerns, `set` remains slow for scripts. The spec is clear: `set` is mutation-only.
**Decision:** Rejected — the whole point is to slim `set`.

### A3: Separate `twig_show` MCP tool (new) vs enhance existing

**Option:** Create a new `twig_show_context` tool instead of modifying the existing `twig_show`.

**Pros:** No breaking change to existing MCP consumers.
**Cons:** Proliferates tools with overlapping responsibilities — the same problem we're solving for CLI.
**Decision:** Enhance existing `twig_show` with optional pending changes enrichment.

---

## Dependencies

### External
- None (all functionality uses existing ADO REST APIs and local SQLite cache)

### Internal
- `SpectreRenderer.BuildStatusViewAsync` / `RenderStatusAsync` — reused without modification
- `ActiveItemResolver` — reused in ShowCommand (currently not a dependency)
- `IAdoGitService` — existing interface, already injected in other commands
- `GitBranchReader` — static utility in `Twig.Infrastructure`, reads `.git/HEAD`
- `PromptStateWriter` — already reads branch via `GitBranchReader`

### Sequencing
- No external blockers. All changes are internal to the twig CLI and MCP server.

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `SetCommand.cs` | Major rewrite (239→~80 lines) | Medium — significant code removal |
| `ShowCommand.cs` | Major enhancement (207→~350 lines) | Medium — new branches, new deps |
| `StatusCommand.cs` | Deletion | Low — clean removal |
| `Program.cs` | Minor edits (3 locations) | Low |
| `CommandRegistrationModule.cs` | Remove 1 line | Low |
| `ContextTools.cs` (MCP) | Remove `twig_status` method | Low |
| `NavigationTools.cs` (MCP) | Enhance `twig_show` | Low |
| `HintEngine.cs` | Update 2 cases | Low |
| `IOutputFormatter.cs` | Add 1 method | Low |
| 4 formatter implementations | Add 1 method each | Low |
| `GroupedHelp` (Program.cs) | Update help text | Low |

### Backward Compatibility

- **CLI:** `twig status` will return "Unknown command" error. No alias.
- **MCP:** `twig_status` tool removed. Consumers must switch to `twig_show`.
- **`twig set` output:** Changes from full dashboard to single confirmation line. Scripts parsing `set` output will break if they depend on the dashboard format.
- **`twig show` signature:** `id` parameter changes from required to optional. Existing `twig show 42` calls continue to work.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `set` output change breaks scripts | Low | Medium | JSON format for `set` is new and well-defined; scripts using `--output json` get a stable schema |
| Missing test coverage after status deletion | Medium | Medium | Migrate behavioral tests from StatusCommand to ShowCommand before deleting |
| `show` no-args branch detection unreliable | Low | Low | Best-effort only; clear error message when no active item and no branch hint |
| Constructor parameter count for ShowCommand grows | Medium | Low | Still within acceptable range (~10 params); CommandContext absorbs cross-cutting concerns |

---

## Open Questions

| # | Question | Severity | Resolution |
|---|----------|----------|------------|
| OQ-1 | Should `set` keep `ContextChangeService.ExtendWorkingSetAsync` for cache warmth? Spec says no sync, but extension is additive and fire-and-forget. | Low | DD-2: Keep it — it's not sync, it's additive cache hydration that benefits follow-up `show` calls. No user-visible output. |
| OQ-2 | Should `twig show --output json` batch mode (`show-batch`) also sync before output? Currently cache-only. | Low | No — spec says batch mode doesn't sync. Keep current behavior. |
| OQ-3 | The spec mentions `--batch` as a flag on `show`. Currently it's a separate `show-batch` command. Should we add `--batch` to `show` as well? | Low | Out of scope for this epic — `show-batch` already works. Can add `--batch` alias later. |
| OQ-4 | Should `StatusResult` domain type be deleted now or deferred? | Low | Deferred — it's in the Domain layer and other components may reference it. Clean up in a follow-up. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tests/Twig.Cli.Tests/Commands/ShowCommand_NoArgsTests.cs` | Tests for `show` no-args mode (active item resolution, branch hints, pending changes, git context) |
| `tests/Twig.Cli.Tests/Commands/SetCommand_SlimTests.cs` | Tests for slimmed `set` (confirmation output, no rendering, no sync) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/SetCommand.cs` | Remove dashboard rendering, child/parent loading, working set computation, sync, hints. Keep resolution, context update, history, prompt state. Add confirmation output. Remove 6 constructor dependencies. |
| `src/Twig/Commands/ShowCommand.cs` | Make `id` optional. Add no-args path (active item resolution, branch detection). Add `IContextStore`, `ActiveItemResolver`, `IPendingChangeStore`, `WorkingSetService` dependencies. Add pending changes + git context enrichment. Fix machine output to sync-first. |
| `src/Twig/Program.cs` | (1) Change smart landing from `["status"]` to `["show"]`. (2) Delete `Status()` method. (3) Change `Show()` signature to make `id` optional. (4) Remove `"status"` from `KnownCommands`. (5) Update grouped help text. |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Remove `services.AddSingleton<StatusCommand>()` |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatSetConfirmation(WorkItem item)` method |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatSetConfirmation` → `Set active item: #42 Title [State]` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatSetConfirmation` → `{"id":42,"title":"...","state":"...","type":"..."}` |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Implement `FormatSetConfirmation` → same as `JsonOutputFormatter` |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatSetConfirmation` → `#42` |
| `src/Twig/Hints/HintEngine.cs` | Change `"status"` case to `"show"`. Update `"set"` hint text from "twig status" to "twig show". |
| `src/Twig.Mcp/Tools/ContextTools.cs` | Delete `Status()` method (lines 92-118) |
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add pending changes to `twig_show` response for active items |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Update tests: remove dashboard assertions, add confirmation output assertions |
| `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | Update for reduced constructor params |
| `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` | Update for reduced constructor params |
| `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | Add pending changes + git context assertions |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | Add sync-first assertions for machine output |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsSetTests.cs` | Minor: remove any status-related assertions |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | Add pending changes enrichment tests |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/StatusCommand.cs` | Command removed per spec |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Tests for deleted command |
| `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Tests for deleted command |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | Tests for deleted MCP tool |

---

## ADO Work Item Structure

### Issue 1: Remove `status` Command and MCP Tool

**Goal:** Completely remove the `status` command from CLI and MCP, including all tests and registration.

**Prerequisites:** None (can be done first as a clean deletion)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1.1 | Delete `StatusCommand.cs` | `src/Twig/Commands/StatusCommand.cs` | S |
| T-1.2 | Remove DI registration and CLI routing | `CommandRegistrationModule.cs`, `Program.cs` (Status method, KnownCommands, help text) | S |
| T-1.3 | Update smart landing to route to `show` | `Program.cs` line 118 | S |
| T-1.4 | Delete `twig_status` MCP tool method | `src/Twig.Mcp/Tools/ContextTools.cs` | S |
| T-1.5 | Delete all status tests | `StatusCommandTests.cs`, `StatusCommand_CacheAwareTests.cs`, `ContextToolsStatusTests.cs` | S |
| T-1.6 | Update HintEngine references | `HintEngine.cs` — rename `"status"` → `"show"`, update `"set"` hint text | S |

**Acceptance Criteria:**
- [ ] `twig status` returns "Unknown command" error
- [ ] `twig_status` MCP tool is not registered
- [ ] `twig` (no args) routes to `show`
- [ ] Solution builds with zero warnings
- [ ] All remaining tests pass

### Issue 2: Slim `set` to Context-Switch Only

**Goal:** Remove all rendering, sync, and enrichment from SetCommand. Output a single confirmation line.

**Prerequisites:** Issue 1 (status deletion removes ambiguity about what `set` should do)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2.1 | Add `FormatSetConfirmation` to `IOutputFormatter` and all 4 implementations | `IOutputFormatter.cs`, `HumanOutputFormatter.cs`, `JsonOutputFormatter.cs`, `JsonCompactOutputFormatter.cs`, `MinimalOutputFormatter.cs` | M |
| T-2.2 | Rewrite `SetCommand.ExecuteCoreAsync` — remove dashboard rendering, sync, hints; add confirmation output | `SetCommand.cs` | M |
| T-2.3 | Remove unused constructor dependencies from SetCommand | `SetCommand.cs` | S |
| T-2.4 | Update SetCommand tests for new behavior | `SetCommandTests.cs`, `SetCommand_ContextChangeTests.cs`, `SetCommandDisambiguationTests.cs` | M |
| T-2.5 | Write new tests for confirmation output formats | `SetCommand_SlimTests.cs` (new) | M |

**Acceptance Criteria:**
- [ ] `twig set 42` outputs `Set active item: #42 Title [State]` (human format)
- [ ] `twig set 42 --output json` outputs `{"id":42,"title":"...","state":"...","type":"..."}`
- [ ] `twig set 42 --output minimal` outputs `#42`
- [ ] SetCommand constructor has ≤7 parameters
- [ ] No child/parent/link loading, no sync, no working set computation
- [ ] Navigation history and prompt state still recorded
- [ ] All existing resolution tests (ID, pattern, disambiguation) still pass

### Issue 3: Enhance `show` — No-Args Mode and Active Item Display

**Goal:** Add no-args mode to `show` that resolves the active item (replacing `status`). Add pending changes and git context enrichment.

**Prerequisites:** Issue 1 (status is gone, show absorbs its role)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-3.1 | Make `id` parameter optional in ShowCommand and Program.cs routing | `ShowCommand.cs`, `Program.cs` | M |
| T-3.2 | Add `IContextStore`, `ActiveItemResolver`, `IPendingChangeStore`, `WorkingSetService` dependencies to ShowCommand | `ShowCommand.cs` | S |
| T-3.3 | Implement no-args path — active item resolution with branch detection hints on miss | `ShowCommand.cs` | L |
| T-3.4 | Add pending changes enrichment to both no-args and by-ID paths | `ShowCommand.cs` | M |
| T-3.5 | Add git context enrichment (branch detection + linked PRs) | `ShowCommand.cs` | M |
| T-3.6 | Fix machine output to sync-first (json, jsonc, minimal, non-TTY human) | `ShowCommand.cs` | M |
| T-3.7 | Write tests for no-args mode | `ShowCommand_NoArgsTests.cs` (new) | L |
| T-3.8 | Update existing show tests for new pending changes and git context | `ShowCommandTests.cs`, `ShowCommand_CacheAwareTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig show` (no args) displays active item with pending changes and git context
- [ ] `twig show` (no args) with no active item shows branch detection hint + exits 1
- [ ] `twig show 42` still works (by-ID, read-only)
- [ ] `twig show --output json` syncs synchronously, then emits complete JSON
- [ ] `twig show --no-refresh` skips sync for all formats
- [ ] Pending changes panel shows field count + note count
- [ ] Git context shows current branch + linked PR status
- [ ] `show-batch` behavior unchanged

### Issue 4: Enhance MCP `twig_show` Tool

**Goal:** Add pending changes enrichment to the MCP `twig_show` tool, replacing the deleted `twig_status`.

**Prerequisites:** Issue 1 (twig_status deleted), Issue 3 (show pattern established)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-4.1 | Add pending changes to `twig_show` response | `NavigationTools.cs`, `McpResultBuilder.cs` | M |
| T-4.2 | Add tests for enhanced `twig_show` with pending changes | `NavigationToolsShowTests.cs` | M |
| T-4.3 | Clean up `ContextToolsTestBase.cs` if status-specific helpers remain | `ContextToolsTestBase.cs` | S |

**Acceptance Criteria:**
- [ ] `twig_show` response includes `pendingChanges` array when item has pending changes
- [ ] `twig_show` response omits `pendingChanges` (or returns empty array) when no changes
- [ ] All MCP tests pass
- [ ] `ContextTools` class only contains `twig_set`

---

## PR Groups

### PG-1: Delete Status Command (Wide)

**Scope:** Complete removal of `status` from CLI and MCP. Pure deletion — no new behavior.

**Tasks:** T-1.1, T-1.2, T-1.3, T-1.4, T-1.5, T-1.6

**Files (~12):**
- `src/Twig/Commands/StatusCommand.cs` (delete)
- `src/Twig/Program.cs` (3 edits: smart landing, delete Status method, update help/known commands)
- `src/Twig/DependencyInjection/CommandRegistrationModule.cs` (1 line removal)
- `src/Twig/Hints/HintEngine.cs` (2 case updates)
- `src/Twig.Mcp/Tools/ContextTools.cs` (delete method)
- `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` (delete)
- `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` (delete)
- `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` (delete)

**Estimated LoC:** ~200 added / ~800 deleted (net reduction)
**Classification:** Wide — many files, mechanical deletions
**Successors:** PG-2 (depends on status being gone)

### PG-2: Slim Set + Enhance Show + MCP (Deep)

**Scope:** Rewrite SetCommand, enhance ShowCommand with no-args mode + pending changes + git context + sync-first machine output. Enhance MCP twig_show.

**Tasks:** T-2.1–T-2.5, T-3.1–T-3.8, T-4.1–T-4.3

**Files (~20):**
- `src/Twig/Commands/SetCommand.cs` (rewrite)
- `src/Twig/Commands/ShowCommand.cs` (major enhancement)
- `src/Twig/Program.cs` (Show signature change)
- `src/Twig/Formatters/IOutputFormatter.cs` (new method)
- `src/Twig/Formatters/HumanOutputFormatter.cs` (new method)
- `src/Twig/Formatters/JsonOutputFormatter.cs` (new method)
- `src/Twig/Formatters/JsonCompactOutputFormatter.cs` (new method)
- `src/Twig/Formatters/MinimalOutputFormatter.cs` (new method)
- `src/Twig.Mcp/Tools/NavigationTools.cs` (enhance twig_show)
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (pending changes in FormatWorkItem)
- `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` (update)
- `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` (update)
- `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` (update)
- `tests/Twig.Cli.Tests/Commands/SetCommand_SlimTests.cs` (new)
- `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` (update)
- `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` (update)
- `tests/Twig.Cli.Tests/Commands/ShowCommand_NoArgsTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` (update)
- `tests/Twig.Mcp.Tests/Tools/ContextToolsTestBase.cs` (cleanup)

**Estimated LoC:** ~1200 added / ~400 deleted
**Classification:** Deep — core behavioral changes in 2 key command files
**Successors:** None

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type | Est. LoC |
|-------|------|----------------|--------------|------|----------|
| PG-1 | `PG-1-delete-status` | Issue 1: T-1.1, T-1.2, T-1.3, T-1.4, T-1.5, T-1.6 | None | Wide | ~200 added / ~800 deleted |
| PG-2 | `PG-2-slim-set-enhance-show-mcp` | Issue 2: T-2.1–T-2.5 · Issue 3: T-3.1–T-3.8 · Issue 4: T-4.1–T-4.3 | PG-1 | Deep | ~1200 added / ~400 deleted |

### Execution Order

**PG-1 first** — pure deletion of `StatusCommand` and `twig_status`. No new behavior. After this PR merges the codebase builds cleanly with no status artifacts. This unblocks PG-2 by eliminating the "two display commands" ambiguity and ensuring the smart landing already routes to `show`.

**PG-2 second** — behavioral rewrites: slim `SetCommand`, enhance `ShowCommand` with no-args mode, pending changes, git context, and sync-first machine output. Also enhances the MCP `twig_show` tool. This is the deep work; it can be reviewed in isolation because PG-1 has already cleared the slate.

### Validation Strategy

**PG-1 — Delete Status (Wide)**
- `dotnet build` with zero warnings (TreatWarningsAsErrors)
- `dotnet test` — all remaining tests pass
- Manual: `twig status` returns unknown-command error
- Manual: `twig` (no args) should route to `show` (after PG-1's smart-landing change)
- Manual: `twig_status` MCP tool absent from tool list

**PG-2 — Slim Set + Enhance Show + MCP (Deep)**
- `dotnet build` with zero warnings
- `dotnet test` — including new `SetCommand_SlimTests` and `ShowCommand_NoArgsTests`
- Behavioral: `twig set 42` outputs single confirmation line in all output formats
- Behavioral: `twig show` (no args) renders active item with pending changes + git context
- Behavioral: `twig show` (no args) with no active item exits 1 with branch hint
- Behavioral: `twig show --output json` syncs before emitting
- Behavioral: `twig show --no-refresh` is cache-only across all formats
- MCP: `twig_show` response includes `pendingChanges` when item has pending changes

---

## References

- [Context Commands Functional Spec](../specs/context-commands.spec.md)
- [Command Layer Bloat Reduction Plan](./command-layer-bloat-reduction.plan.md) — established `CommandContext` pattern
- StatusCommand.cs (current): 245 lines
- SetCommand.cs (current): 239 lines  
- ShowCommand.cs (current): 207 lines

