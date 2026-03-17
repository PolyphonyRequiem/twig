---
goal: Prompt state file for zero-latency shell prompt integration
version: 1.0
date_created: 2026-03-17
last_updated: 2026-03-17
owner: Twig CLI team
tags: [feature, cli, ux, prompt, oh-my-posh, performance, refactor]
revision_notes: "v1.0 — Initial plan. Replaces per-prompt subprocess invocation with a pre-computed state file written by mutating commands."
---

# Prompt State File

## Executive Summary

The current Oh My Posh integration runs `twig _prompt` on every shell prompt render — spawning a native AOT process (~22ms startup), initializing SQLitePCL, loading config, opening SQLite, executing 3 queries, and formatting output. Even with a fast-path bypass of the DI pipeline, this takes ~180ms on a warm AOT binary. On non-AOT builds (development), it exceeds 300ms.

This plan replaces the per-prompt subprocess with a **pre-computed state file** (`.twig/prompt.json`). Every twig command that modifies prompt-visible state writes this file as a side effect. The shell hook reads the file (~1ms) and exports environment variables for Oh My Posh. The `_prompt` command is removed entirely — the state file IS the prompt mechanism.

**Performance target**: Shell hook prompt read < 2ms (file read + JSON parse in shell).

---

## Problem Statement

1. **Process spawn overhead**: `twig _prompt` takes ~180ms AOT, ~300ms JIT — perceptible in prompt rendering. Oh My Posh documentation recommends prompt segments complete in <50ms.
2. **Redundant computation**: Every prompt render re-reads config, re-opens SQLite, re-queries 3 tables, and re-resolves colors/icons — all for data that only changes when a twig command runs.
3. **Wasted on no-ops**: In most prompt renders, nothing has changed since the last render. The data is re-computed identically.

---

## Design

### Core Concept

Twig commands that modify prompt-visible state write `.twig/prompt.json` after completing their primary operation. The shell hook reads this file and sets environment variables. Oh My Posh reads the env vars.

```
┌─────────────────────────────────────────────────────────────────┐
│  twig set 12345                                                 │
│  ┌────────────┐    ┌───────────┐    ┌──────────────────────┐   │
│  │ SetCommand  │───▶│  SQLite   │───▶│ .twig/prompt.json    │   │
│  │ (normal DI) │    │  (write)  │    │ (write after commit) │   │
│  └────────────┘    └───────────┘    └──────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  Shell prompt render (every keypress)                           │
│  ┌──────────────────┐    ┌─────────────┐    ┌───────────────┐  │
│  │ Set-PoshContext   │───▶│ cat/gc file │───▶│ $env:TWIG_*   │  │
│  │ (shell function)  │    │ (~1ms)      │    │ (env vars)    │  │
│  └──────────────────┘    └─────────────┘    └───────────────┘  │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Oh My Posh text segment                                   │  │
│  │ template: "{{ .Env.TWIG_PROMPT }}"                        │  │
│  │ foreground_templates: ["{{ .Env.TWIG_TYPE_COLOR }}"]       │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### File Location

`.twig/prompt.json` — lives alongside `config` in the `.twig/` root. Not context-scoped (org/project) because:
- The prompt shows the **current** active item, which is already resolved per-cwd
- One file per workspace keeps the shell hook simple (`$PWD/.twig/prompt.json`)
- When context changes (e.g., `twig init` with different org/project), the writing command regenerates it

### File Schema

```json
{
  "text": "◆ #12345 Implement login… [Active] •",
  "id": 12345,
  "type": "Epic",
  "typeBadge": "◆",
  "title": "Implement login",
  "state": "Active",
  "stateCategory": "InProgress",
  "isDirty": true,
  "typeColor": "#8B00FF",
  "stateColor": null,
  "branch": "feature/12345-implement-login",
  "generatedAt": "2026-03-17T14:23:45Z"
}
```

When no active work item is set (context cleared), the file contains:
```json
{}
```

Field descriptions:

| Field | Type | Description |
|-------|------|-------------|
| `text` | string | Pre-formatted plain-text prompt string (badge + id + title + state + dirty) |
| `id` | int | Work item ID |
| `type` | string | Work item type name (e.g., "Epic", "Bug", "Task") |
| `typeBadge` | string | Type icon glyph resolved using current `display.icons` config |
| `title` | string | Truncated title (default max-width: 40) |
| `state` | string | Raw ADO state string (e.g., "Active", "Committed", "Resolved") |
| `stateCategory` | string | Normalized category: Proposed, InProgress, Resolved, Completed, Removed, Unknown |
| `isDirty` | bool | Whether the work item has unsaved local changes |
| `typeColor` | string? | Hex color for the work item type (from `display.typeColors` or `TypeAppearances`) |
| `stateColor` | string? | Reserved for future state-based coloring |
| `branch` | string? | Current git branch (from `.git/HEAD` file I/O, not subprocess) |
| `generatedAt` | string | ISO 8601 timestamp of when the file was written (for staleness detection) |

### Shell Hook (PowerShell)

```powershell
function Set-PoshContext {
    $f = Join-Path $PWD ".twig" "prompt.json"
    if (Test-Path $f) {
        $p = Get-Content $f -Raw | ConvertFrom-Json
        $env:TWIG_PROMPT = $p.text
        $env:TWIG_TYPE_COLOR = $p.typeColor
        $env:TWIG_STATE_CATEGORY = $p.stateCategory
    } else {
        $env:TWIG_PROMPT = ""
        $env:TWIG_TYPE_COLOR = ""
        $env:TWIG_STATE_CATEGORY = ""
    }
}
```

### Shell Hook (bash/zsh)

```bash
set_poshcontext() {
    local f="$PWD/.twig/prompt.json"
    if [ -f "$f" ]; then
        export TWIG_PROMPT=$(jq -r '.text // empty' "$f" 2>/dev/null)
        export TWIG_TYPE_COLOR=$(jq -r '.typeColor // empty' "$f" 2>/dev/null)
        export TWIG_STATE_CATEGORY=$(jq -r '.stateCategory // empty' "$f" 2>/dev/null)
    else
        export TWIG_PROMPT=""
    fi
}
```

### Oh My Posh Segment (with dynamic colors)

```json
{
  "type": "text",
  "style": "powerline",
  "powerline_symbol": "\ue0b0",
  "foreground": "#FFFFFF",
  "foreground_templates": [
    "{{ if .Env.TWIG_TYPE_COLOR }}{{ .Env.TWIG_TYPE_COLOR }}{{ end }}"
  ],
  "background": "#333333",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}"
}
```

---

## Commands That Write prompt.json

A command writes `prompt.json` if it modifies **any** prompt-visible field: active context, work item state/title, dirty flag, or display config affecting badge/color rendering.

### Active Context Mutations

| Command | Mutation | Trigger |
|---------|----------|---------|
| `set` | Sets active work item | Always |
| `up` / `down` | Sets active work item (delegates to `set`) | Always |
| `flow-start` | Sets active work item + state transition | Always |
| `flow-close` | Clears active work item | Always (writes `{}`) |
| `stash pop` | Sets active work item (if WI# detected in stash) | When WI# found |
| `_hook post-checkout` | Sets active work item from branch name | When WI# extracted |

### Work Item State/Field Mutations

| Command | Mutation | Trigger |
|---------|----------|---------|
| `state` | Transitions state (e.g., Active → Resolved) | Always |
| `flow-done` | Transitions to Resolved | Always |
| `branch` | Transitions Proposed → Active (if `autotransition` on) | When auto-transition fires |
| `update` | Changes a field value | When field is title (rare but possible) |
| `refresh` | Overwrites cache from ADO (state, title may change) | Always |

### Dirty Flag Mutations

| Command | Mutation | Trigger |
|---------|----------|---------|
| `save` | Clears dirty flag (pushes changes to ADO) | Always |
| `edit` | Marks dirty (adds pending field changes) | Always |
| `note` | Marks dirty (adds pending note) | Always |

### Display Config Mutations

| Command | Mutation | Trigger |
|---------|----------|---------|
| `config display.icons` | Changes icon mode (unicode ↔ nerd) | When `display.icons` is the key |
| `config display.typecolors.*` | Changes type color | When any `display.typecolors` key is set |

### Not Applicable (no prompt.json write needed)

| Command | Reason |
|---------|--------|
| `status`, `tree`, `ws`, `sprint`, `show`, `log`, `context` | Read-only |
| `branch` (without auto-transition) | No prompt-visible change |
| `commit`, `pr` | Adds artifact links only — no prompt-visible state |
| `stash` (push) | Git-only, no twig state change |
| `hooks install/uninstall` | Git hooks, not twig state |
| `init` | Creates config; no active work item yet |
| `seed` | Creates child item; doesn't change active context |
| `version`, `upgrade`, `changelog` | Utility commands |
| `ohmyposh init` | Generates snippets, no state change |

---

## Implementation

### Architecture: PromptStateWriter Service

A new `IPromptStateWriter` interface with a single method:

```csharp
public interface IPromptStateWriter
{
    void WritePromptState();
}
```

The implementation (`PromptStateWriter`) is registered as a singleton in DI. It:
1. Reads the active work item ID from `IContextStore`
2. If no active item → writes `{}` to `.twig/prompt.json`
3. If active item → reads work item from `IWorkItemRepository`, resolves badge/color/category/branch, writes full JSON
4. Uses `Utf8JsonWriter` (AOT-compatible, no reflection)
5. Writes atomically (write to `.twig/prompt.json.tmp`, then `File.Move` with overwrite)

Commands call `_promptStateWriter.WritePromptState()` at the end of their `ExecuteAsync` methods — after all primary operations succeed.

### `_prompt` Command Removed

The `_prompt` command is deleted entirely (EPIC-003). The shell hook reads `.twig/prompt.json` directly — no twig process is spawned at prompt render time. Helper utilities (`GitBranchReader`, `TruncateTitle`, `StateCategoryResolver`) are reused by `PromptStateWriter`.

---

## Implementation Plan

### EPIC-001: PromptStateWriter service — DONE

**Goal**: Create the `IPromptStateWriter` service that generates and writes `.twig/prompt.json`.

**Prerequisites**: None — uses existing infrastructure (IContextStore, IWorkItemRepository, TwigConfiguration, IconSet, TypeColorResolver, StateCategoryResolver, GitBranchReader).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `IPromptStateWriter` interface with `void WritePromptState()` method. Place in Domain interfaces. | `src/Twig.Domain/Interfaces/IPromptStateWriter.cs` | DONE |
| ITEM-002 | IMPL | Create `PromptStateWriter` implementation. Dependencies: `IContextStore`, `IWorkItemRepository`, `TwigConfiguration`, `TwigPaths`, `IProcessTypeStore`. Reads active work item, resolves badge (via `IconSet`), color (via `TypeColorResolver`), state category (via `StateCategoryResolver`), branch (via `GitBranchReader`), and dirty flag (from `WorkItem.IsDirty`). Formats `text` field using same pattern as `PromptCommand.FormatPlain()`. Writes JSON atomically using `Utf8JsonWriter` to `.twig/prompt.json.tmp` then `File.Move(tmp, target, overwrite: true)`. Writes `{}` when no active work item. Catches all exceptions silently — prompt state write MUST NOT fail the parent command. | `src/Twig.Infrastructure/Config/PromptStateWriter.cs` | DONE |
| ITEM-003 | IMPL | ~~Add `PromptStateData` record type~~ — Removed per review: `PromptStateWriter` uses `Utf8JsonWriter` directly, making the record dead code. The `Utf8JsonWriter` approach explicitly writes null fields, which is the correct behavior for prompt consumers. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE (N/A) |
| ITEM-004 | IMPL | Register `IPromptStateWriter` / `PromptStateWriter` as singleton in `TwigServiceRegistration.cs` (shared DI) so all commands can access it. | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE |
| ITEM-005 | TEST | Unit tests for `PromptStateWriter`: (a) writes valid JSON when active item exists, (b) writes `{}` when no active item, (c) includes correct `typeBadge` for unicode mode, (d) includes correct `typeBadge` for nerd mode, (e) includes `typeColor` from `display.typeColors` config, (f) includes `typeColor` from `TypeAppearances` fallback, (g) `text` field matches expected plain format, (h) `isDirty` reflects work item state, (i) `branch` populated from `.git/HEAD`, (j) `branch` is null when detached HEAD, (k) atomic write — partial write does not corrupt file, (l) exception in writer does not propagate. | `tests/Twig.Cli.Tests/Commands/PromptStateWriterTests.cs` | DONE |

**Acceptance Criteria**:

- [x] `PromptStateWriter.WritePromptState()` creates `.twig/prompt.json` with all schema fields
- [x] File is written atomically (tmp + move)
- [x] Writer exceptions are swallowed — never fails the parent command
- [x] Works with both legacy flat and nested context DB paths
- [x] `typeBadge` uses current `display.icons` config
- [x] All tests pass

**Completion Notes**: Addressed four review issues: (1) badge resolution uses `IconSet.ResolveTypeBadge()` with full 4-step chain + first-char fallback; (2) removed dead `PromptStateData` record and added schema doc comment to `WriteFullState`; (3) added `ProcessTypeStoreException_FallsThroughToHeuristic` test; (4) removed unused `InfraGitBranchReader` alias. Updated `TypeBadge_Nerd_Epic` test to exercise ADO icon ID path via `TypeAppearances`. Added `TypeBadge_CustomType_UsesFirstCharFallback` test.

---

### EPIC-002:Wire prompt state writes into mutating commands

**Goal**: Every command that modifies prompt-visible state calls `_promptStateWriter.WritePromptState()` after its primary operation.

**Prerequisites**: EPIC-001.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-006 | IMPL | Add `IPromptStateWriter` parameter to `SetCommand` constructor. Call `WritePromptState()` after `SetActiveWorkItemIdAsync()` succeeds. | `src/Twig/Commands/SetCommand.cs` | TO DO |
| ITEM-007 | IMPL | Add `IPromptStateWriter` to `NavigationCommands` (Up/Down). These delegate to `SetCommand` which now writes — verify no double-write needed, or call at the navigation level if `SetCommand` is called internally without the writer. | `src/Twig/Commands/NavigationCommands.cs` | TO DO |
| ITEM-008 | IMPL | Add `IPromptStateWriter` to `FlowStartCommand`. Call after all operations (set context, state transition, assign, branch) complete. Single write at the end captures final state. | `src/Twig/Commands/FlowStartCommand.cs` | TO DO |
| ITEM-009 | IMPL | Add `IPromptStateWriter` to `FlowDoneCommand`. Call after state transition (InProgress → Resolved). | `src/Twig/Commands/FlowDoneCommand.cs` | TO DO |
| ITEM-010 | IMPL | Add `IPromptStateWriter` to `FlowCloseCommand`. Call after `ClearActiveWorkItemIdAsync()` — writes `{}`. | `src/Twig/Commands/FlowCloseCommand.cs` | TO DO |
| ITEM-011 | IMPL | Add `IPromptStateWriter` to `StateCommand`. Call after state transition completes. | `src/Twig/Commands/StateCommand.cs` | TO DO |
| ITEM-012 | IMPL | Add `IPromptStateWriter` to `SaveCommand`. Call after `ClearChangesAsync()` — dirty flag changes. | `src/Twig/Commands/SaveCommand.cs` | TO DO |
| ITEM-013 | IMPL | Add `IPromptStateWriter` to `EditCommand` and `NoteCommand`. Call after `AddChangeAsync()` — dirty flag set. | `src/Twig/Commands/EditCommand.cs`, `src/Twig/Commands/NoteCommand.cs` | TO DO |
| ITEM-014 | IMPL | Add `IPromptStateWriter` to `RefreshCommand`. Call after cache refresh completes — active item state/title may have changed. | `src/Twig/Commands/RefreshCommand.cs` | TO DO |
| ITEM-015 | IMPL | Add `IPromptStateWriter` to `HookHandlerCommand`. Call after post-checkout sets context. Only write on post-checkout, not on prepare-commit-msg or commit-msg. | `src/Twig/Commands/HookHandlerCommand.cs` | TO DO |
| ITEM-016 | IMPL | Add `IPromptStateWriter` to `StashCommand`. Call after `PopAsync` sets context (when WI# detected in stash). | `src/Twig/Commands/StashCommand.cs` | TO DO |
| ITEM-017 | IMPL | Add `IPromptStateWriter` to `ConfigCommand`. Call after writing any `display.*` key — badge, color, or other display settings may affect prompt rendering. | `src/Twig/Commands/ConfigCommand.cs` | TO DO |
| ITEM-018 | IMPL | Add `IPromptStateWriter` to `BranchCommand`. Call when auto-transition fires (state changes from Proposed to Active). | `src/Twig/Commands/BranchCommand.cs` | TO DO |
| ITEM-019 | IMPL | Add `IPromptStateWriter` to `UpdateCommand`. Call after field update completes — title may have changed. | `src/Twig/Commands/UpdateCommand.cs` | TO DO |
| ITEM-020 | IMPL | Update DI registrations in `Program.cs` to inject `IPromptStateWriter` into all modified command constructors. | `src/Twig/Program.cs` | TO DO |
| ITEM-021 | TEST | Integration tests verifying prompt.json is written after: (a) `set`, (b) `flow-start`, (c) `flow-close` (writes `{}`), (d) `state` transition, (e) `save` (dirty cleared), (f) `config display.icons nerd` (badge changes). Use temp directories with `.twig/` setup. | `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | TO DO |

**Acceptance Criteria**:

- [ ] `twig set 12345` writes `.twig/prompt.json` with work item #12345 data
- [ ] `twig flow-close` writes `{}` to `.twig/prompt.json`
- [ ] `twig state s` updates state in `prompt.json`
- [ ] `twig save` updates `isDirty: false` in `prompt.json`
- [ ] `twig config display.icons nerd` regenerates `prompt.json` with nerd font badge
- [ ] `_hook post-checkout` writes `prompt.json` on branch switch
- [ ] Prompt state write failure does not cause command failure
- [ ] All tests pass
- [ ] AOT build succeeds

---

### EPIC-003: Remove `_prompt` command

**Goal**: Remove the `_prompt` command entirely. The state file is the prompt mechanism — there is no need for a command. The shell hook reads `.twig/prompt.json` directly.

**Prerequisites**: EPIC-002 (all mutating commands write the state file).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-022 | IMPL | Remove the `_prompt` fast-path block from `Program.cs` (the `if (args[0] == "_prompt")` section). | `src/Twig/Program.cs` | TO DO |
| ITEM-023 | IMPL | Remove the `Prompt()` method and `[Command("_prompt")]` attribute from `TwigCommands` in `Program.cs`. Remove `PromptCommand` DI registration. | `src/Twig/Program.cs` | TO DO |
| ITEM-024 | IMPL | Delete `PromptCommand.cs`. The `PromptBadges`, `GitBranchReader`, `TruncateTitle`, and `GetStateCategory` helpers that are reused by `PromptStateWriter` should be extracted to shared locations or inlined in the writer (EPIC-001 handles this). | `src/Twig/Commands/PromptCommand.cs` | TO DO |
| ITEM-025 | IMPL | Delete or update `PromptCommandTests.cs` — remove tests for the deleted command. Prompt behavior is now tested via `PromptStateWriter` tests (EPIC-001) and integration tests (EPIC-002). | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | TO DO |
| ITEM-026 | IMPL | Remove `prompt` / `_prompt` from README command table if still present. | `README.md` | TO DO |

**Acceptance Criteria**:

- [ ] `twig _prompt` returns "unknown command" error
- [ ] `_prompt` does not appear in `--help` output
- [ ] No `PromptCommand` class exists in the codebase
- [ ] All tests pass
- [ ] AOT build succeeds

---

### EPIC-004: Update `ohmyposh init` and documentation

**Goal**: Update the `ohmyposh init` helper to generate shell hooks that read `prompt.json` instead of running `twig _prompt`. Update documentation.

**Prerequisites**: EPIC-001, EPIC-002.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-027 | IMPL | Update `OhMyPoshCommands.Init()` to generate shell hooks that read `.twig/prompt.json` and set `$env:TWIG_PROMPT`, `$env:TWIG_TYPE_COLOR`, `$env:TWIG_STATE_CATEGORY`. Support `--shell pwsh|bash|zsh|fish`. PowerShell uses `Get-Content | ConvertFrom-Json`. Bash/zsh uses `jq` with fallback note. Fish uses `jq` or string parsing. | `src/Twig/Commands/OhMyPoshCommands.cs` | TO DO |
| ITEM-028 | IMPL | Update OMP JSON segment snippets to include `foreground_templates` using `TWIG_TYPE_COLOR` env var for dynamic type coloring. | `src/Twig/Commands/OhMyPoshCommands.cs` | TO DO |
| ITEM-029 | IMPL | Update `docs/ohmyposh.md` to document the state file approach: how it works, that prompt data updates automatically on twig commands, troubleshooting (stale state = run any twig command like `twig status` to regenerate). | `docs/ohmyposh.md` | TO DO |
| ITEM-030 | IMPL | Update `docs/examples/twig.omp.json` with `foreground_templates` using `TWIG_TYPE_COLOR`. | `docs/examples/twig.omp.json` | TO DO |
| ITEM-031 | TEST | Update `OhMyPoshCommands` tests: (a) PowerShell hook reads `prompt.json` not `twig _prompt`, (b) bash hook reads `prompt.json`, (c) segment JSON includes `foreground_templates`. | `tests/Twig.Cli.Tests/Commands/OhMyPoshCommandsTests.cs` | TO DO |

**Acceptance Criteria**:

- [ ] `twig ohmyposh init --shell pwsh` outputs hook that reads `prompt.json`
- [ ] `twig ohmyposh init --shell bash` outputs hook that reads `prompt.json`
- [ ] Generated OMP segment includes `foreground_templates` for type coloring
- [ ] Documentation explains state file approach (no subprocess needed)
- [ ] Troubleshooting covers: stale data (run any twig command to refresh)
- [ ] All tests pass

---

## Risks and Mitigations

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| `prompt.json` goes stale (e.g., state changed in ADO web UI) | Medium | Low | `twig refresh` writes prompt.json. Any mutating twig command regenerates it. OMP segment gracefully handles stale data. |
| File write failure (permissions, disk full) | Low | Low | Writer catches all exceptions. Parent command succeeds regardless. Prompt just shows stale data. |
| Race condition: two twig commands writing simultaneously | Very Low | Low | Atomic write (tmp + move) prevents corruption. Last writer wins — both are writing valid state. |
| Shell hook performance with `ConvertFrom-Json` / `jq` | Low | Low | Single small JSON file (<500 bytes). PowerShell `ConvertFrom-Json` is <2ms. `jq` is <1ms. |
| Bash/zsh users without `jq` installed | Medium | Medium | Provide a fallback hook that uses `python3 -c` or plain `grep`/`sed`. |

---

## Open Questions

All resolved:

1. **Q-001** ✅: `prompt.json` includes `generatedAt` ISO 8601 timestamp for staleness detection.
2. **Q-002** ✅: `config` command regenerates `prompt.json` on **all** `display.*` key changes.
3. **Q-003** ✅: `_prompt` command is removed entirely. The state file is the mechanism — no subprocess needed. Any mutating twig command refreshes the file.

---

## References

- [Oh My Posh — Templates](https://ohmyposh.dev/docs/configuration/templates) — `{{ .Env.VarName }}` access, `Set-PoshContext` hook
- [Oh My Posh — Text Segment](https://ohmyposh.dev/docs/segments/system/text) — `text` segment for env var display
- [Oh My Posh — Colors](https://ohmyposh.dev/docs/configuration/colors) — `foreground_templates`, `background_templates`
- [twig-ohmyposh.plan.md](docs/projects/twig-ohmyposh.plan.md) — Original OMP integration plan (predecessor)
