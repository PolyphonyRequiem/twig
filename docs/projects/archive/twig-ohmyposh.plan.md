---
goal: Oh My Posh segment integration for Twig CLI prompt context
version: 3.0
date_created: 2026-03-15
last_updated: 2026-03-15
owner: Twig CLI team
tags: [feature, cli, ux, prompt, oh-my-posh, integration]
revision_notes: "v3.0 — Corrected factually incorrect Claude Code segment citation in Executive Summary (Claude Code is a native Go segment, not a text segment reading an env var). Added .Branch field to PromptData, FR-004, data flow, and JSON output per original requirements (uses git HEAD file parsing, no subprocess). Added cache property to all three JSON snippet examples (powerline, plain, diamond) to match architecture diagram. Fixed data flow step 2 wording from 'Load' to 'Use' for DI-injected config. Added RD-009 for resolved state color discrepancy between twig status (green) and prompt segment (blue). Prior v2.0 changes preserved."
---

# Introduction

This document describes the solution design and implementation plan for integrating the Twig CLI with [Oh My Posh](https://ohmyposh.dev/), a cross-platform prompt theme engine. The integration enables developers to see their current Azure DevOps work item context — ID, type badge, title snippet, state, and dirty indicator — directly in their shell prompt without running any command.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: Functional requirements use `FR-` prefix, non-functional requirements use `NFR-`, failure modes use `FM-`, and acceptance criteria use `AC-`. These enable traceability across sections.

---

## Executive Summary

Developers using Twig today must run `twig status` to see what work item they're focused on. This plan adds an Oh My Posh integration so the active work item appears in the shell prompt automatically. The design adds a `twig prompt` CLI command that reads exclusively from the local SQLite cache (never the network) with a <50ms execution target. The integration uses Oh My Posh's **environment variable + `text` segment** pattern: a shell pre-prompt hook (`Set-PoshContext` in PowerShell, `set_poshcontext` in bash/zsh/fish/nushell) runs `twig prompt` before each prompt render, stores the output in `$env:TWIG_PROMPT`, and a `text` segment reads it via `{{ .Env.TWIG_PROMPT }}`. A `twig ohmyposh init` helper command generates both the shell hook function and the Oh My Posh JSON segment snippet. This approach is cross-shell, avoids subprocess spawning at prompt render time (OMP reads an env var, not a process), and follows the documented `Set-PoshContext` / `set_poshcontext` hook mechanism for populating environment variables before prompt rendering (see [OMP Templates — Environment variables](https://ohmyposh.dev/docs/configuration/templates#environment-variables)).

---

## Background

### Current system state

The Twig CLI is a .NET 9 Native AOT executable built with ConsoleAppFramework v5.7.13. Its architecture relevant to this plan:

- **`TwigConfiguration`** (`src/Twig.Infrastructure/Config/TwigConfiguration.cs`) — POCO loaded from `.twig/config` JSON. Contains `DisplayConfig` (with `Icons: string = "unicode"`, `TypeColors: Dictionary<string, string>?`) as a nested property, and `TypeAppearances: List<TypeAppearanceConfig>?` as a **top-level property** on `TwigConfiguration` directly (line 19).
- **`TwigPaths`** (`src/Twig.Infrastructure/Config/TwigPaths.cs`) — resolves `.twig/` directory and context-specific DB path: `.twig/{org}/{project}/twig.db`. The static method `ForContext(twigDir, org, project)` builds the nested path; for repositories where `config.Organization` or `config.Project` is empty, `Program.cs` falls back to the flat path `.twig/twig.db`.
- **`IContextStore`** (`src/Twig.Domain/Interfaces/IContextStore.cs`) — stores the active work item ID in the `context` table. Key method: `GetActiveWorkItemIdAsync()`. The active work item key is `"active_work_item_id"`.
- **`IWorkItemRepository`** (`src/Twig.Domain/Interfaces/IWorkItemRepository.cs`) — reads work items from SQLite. Key method: `GetByIdAsync(int id)`.
- **`WorkItem`** (`src/Twig.Domain/Aggregates/WorkItem.cs`) — root aggregate with `Id`, `Type` (WorkItemType), `Title`, `State`, `IsDirty` (bool, line 41), `AssignedTo`, `ParentId`. The `IsDirty` flag is persisted to the `is_dirty` column in the `work_items` table and restored via `SetDirty()` during `MapRow()`.
- **`WorkItemType`** (`src/Twig.Domain/ValueObjects/WorkItemType.cs`) — `readonly record struct` with `Value: string`. Static instances for `Epic`, `Feature`, `UserStory`, `ProductBacklogItem`, `Bug`, `Task`, `Impediment`, `Requirement`, `ChangeRequest`, `Review`, `Risk`, `Issue`, `TestCase`.
- **`HumanOutputFormatter`** (`src/Twig/Formatters/HumanOutputFormatter.cs`) — has a `private static GetTypeBadge(WorkItemType)` method that returns **Unicode-only** badges: `◆` (Epic), `▪` (Feature), `●` (UserStory/PBI/Requirement), `✦` (Bug/Impediment/Risk), `□` (Task/TestCase/etc.), `■` (fallback). This method does **not** reference `display.icons` and has **no nerd font branch**. The nerd font `IconSet` class from the prior nerd-font-icons plan has not been implemented.
- **`HumanOutputFormatter.GetTypeColor()`** — instance method (not static) that maps types to ANSI colors, with hex true-color override from the constructor-injected `_typeColors` dictionary (sourced from `config.Display.TypeColors`).
- **`SqliteCacheStore`** (`src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`) — manages SQLite lifecycle with WAL mode and `busy_timeout=5000`. Schema version 2. DDL includes `work_items` table with `is_dirty INTEGER NOT NULL DEFAULT 0` column and a partial index `idx_work_items_dirty ON work_items(is_dirty) WHERE is_dirty = 1`.
- **`TwigCommands`** (`src/Twig/Program.cs`) — all CLI commands routed through this class via `ConsoleAppFramework`. Commands are resolved lazily from DI. The class uses flat method routing (each public method = one command).
- **`TwigJsonContext`** (`src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`) — source-generated JSON context for AOT-compatible serialization.
- **DB path resolution in `Program.cs`** (lines 29-31) — conditional: if `config.Organization` and `config.Project` are both non-empty, uses `TwigPaths.ForContext()`; otherwise falls back to `new TwigPaths(twigDir, configPath, Path.Combine(twigDir, "twig.db"))`.

### Prior art

- The `display.icons` config key supports `"unicode"` and `"nerd"` modes. However, `HumanOutputFormatter.GetTypeBadge()` is `private static` and returns only Unicode glyphs unconditionally — it does not branch on `display.icons`. The nerd font `IconSet` class (referenced in `docs/projects/twig-nerd-font-icons.plan.md`) has **not been implemented**. The prompt command MUST create a new static utility method for badge resolution that accepts the icon mode as a parameter.
- The `display.typeColors` config (on `DisplayConfig`) stores hex color values per work item type (e.g., `{"Epic": "#8B00FF", "Bug": "#CC293D"}`).
- The `TypeAppearances` config (on `TwigConfiguration` directly, **not** on `DisplayConfig`) stores ADO-sourced type metadata including `Color` (hex) and `IconId`.
- The `FormatterHelpers.GetShorthand()` maps states to single-character codes: `p` (new/to do/proposed), `c` (active/doing/committed/in progress/approved), `s` (resolved), `d` (closed/done), `x` (removed).
- The `work_items` table DDL includes `is_dirty INTEGER NOT NULL DEFAULT 0`, maintained by `SqliteWorkItemRepository.SaveAsync()` via the `@isDirty` parameter. This eliminates the need to query `pending_changes` for dirty detection.

### Oh My Posh integration patterns

Oh My Posh provides two viable integration approaches for external tools:

1. **Environment variable + `text` segment** (selected) — A shell pre-prompt hook function (`Set-PoshContext` in PowerShell, `set_poshcontext` in bash/zsh, `set_poshcontext` fish function, `set_poshcontext` in nushell) runs before each prompt render. The hook executes `twig prompt` and stores stdout in an environment variable (e.g., `TWIG_PROMPT`). A `text` segment reads it via `{{ .Env.TWIG_PROMPT }}`. This is the documented mechanism for populating environment variables before prompt rendering (see [OMP Templates — Environment variables](https://ohmyposh.dev/docs/configuration/templates#environment-variables)). It is cross-shell and documented for PowerShell, Zsh, Bash, Fish, and Nushell. **Note**: The Claude Code segment (`type: "claude"`) uses a different approach — it is a **native Go segment** compiled into the OMP binary that reads `POSH_CLAUDE_STATUS`. It is **not** an example of the env var + text segment pattern. The `Set-PoshContext` / `set_poshcontext` hook is documented generically for any env var population use case, not specifically for Claude Code.

2. **Native Go segment** — A segment written in Go and compiled into the Oh My Posh binary. Provides structured template variables (`.Property` syntax). Requires a PR to the Oh My Posh repository.

**Note**: Oh My Posh does **not** have a `command` segment type. The `type` property in the segment config refers to built-in segment types (e.g., `"text"`, `"git"`, `"path"`, `"claude"`). There is no `"command"` value in the segment type enum, no `{{ .Output }}` template variable, and no `shell`/`command` properties on segments. Verification against the OMP source (`src/config/segment_types.go`) and the official JSON schema (`themes/schema.json`) confirms this.

---

## Problem Statement

1. **No prompt visibility**: Developers must run `twig status` explicitly to see their active work item. This interrupts flow and adds cognitive load, especially when switching between repositories.
2. **No shell integration**: Twig has no command optimized for prompt use — `twig status` performs pending-change analysis, seed-staleness checks, and hint generation, making it too slow and verbose for prompt rendering.
3. **No Oh My Posh support**: There are no sample configurations or helper commands for the most popular cross-platform prompt engine.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Add a `twig prompt` command that outputs a compact, single-line summary of the active work item context, optimized for shell prompt rendering (<50ms).
2. **G-2**: Support `--format plain|json` flag on `twig prompt` to output either pre-formatted text or structured JSON.
3. **G-3**: Support `--max-width <int>` flag to control title truncation.
4. **G-4**: Provide sample Oh My Posh theme snippets using the environment variable + `text` segment approach with cross-shell hook functions.
5. **G-5**: Add a `twig ohmyposh init` helper command that outputs both the shell hook function and the Oh My Posh JSON segment snippet.
6. **G-6**: Ensure graceful degradation — empty output when Twig is not initialized, cache is empty, or no active work item.
7. **G-7**: Ensure all behavior works under Native AOT compilation.

### Non-Goals

- **NG-1**: Contributing a native Go segment to the Oh My Posh repository — deferred until demand warrants the maintenance burden.
- **NG-2**: Building a standalone external segment executable — Oh My Posh does not support external segment executables as a first-class feature.
- **NG-3**: Automatically patching the user's Oh My Posh config file — too risky; we output the snippet and let the user paste it.
- **NG-4**: Supporting Starship, Tide, or other prompt engines — deferred to future plans.
- **NG-5**: Network calls from the prompt command — explicitly prohibited for performance.
- **NG-6**: Version updates or changelog entries — only when explicitly instructed.

---

## Requirements

### Functional Requirements

- **FR-001**: `twig prompt` MUST output a single-line summary of the active work item when one is set.
- **FR-002**: The output MUST include: work item ID, type badge (unicode or nerd based on `display.icons` config), truncated title, state name, and dirty indicator.
- **FR-003**: `twig prompt` MUST output nothing (empty string, exit code 0) when: (a) `.twig/` directory does not exist, (b) no active work item is set, (c) the active work item is not in the cache.
- **FR-004**: `twig prompt --format json` MUST output a JSON object with keys: `id` (int), `type` (string), `typeBadge` (string), `title` (string), `state` (string), `stateCategory` (string — one of: `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`), `isDirty` (bool), `color` (string|null), `branch` (string|null — current git branch if HEAD is a symbolic ref, null otherwise).
- **FR-005**: `twig prompt --max-width <int>` MUST truncate the title with `…` suffix to fit within the specified width budget. Default max-width: 40.
- **FR-006**: `twig ohmyposh init` MUST output: (a) a shell hook function for the user's shell that sets `$env:TWIG_PROMPT` / `$TWIG_PROMPT` by running `twig prompt`, and (b) a JSON snippet for an Oh My Posh `text` segment configured to read `{{ .Env.TWIG_PROMPT }}`.
- **FR-007**: `twig ohmyposh init --style powerline|plain|diamond` MUST output the snippet with the corresponding segment style.
- **FR-008**: The `--format plain` (default) output MUST be a single line in the format: `{badge} #{id} {title} [{state}]{dirty}` where `{dirty}` is ` •` if dirty, empty otherwise.
- **FR-009**: `twig ohmyposh init --shell pwsh|bash|zsh|fish` MUST output the appropriate shell hook function syntax for the specified shell.

### Non-Functional Requirements

- **NFR-001**: `twig prompt` MUST complete in <50ms total execution time. MUST read only from local SQLite cache. MUST NOT make any network calls.
- **NFR-002**: If the SQLite database is locked (e.g., concurrent `twig save`), the command SHOULD return empty output rather than blocking. The existing `busy_timeout=5000` SHOULD be reduced for prompt mode — a separate read-only connection with `busy_timeout=100` SHOULD be used.
- **NFR-003**: `twig prompt` MUST be AOT-compatible. All JSON output MUST use `Utf8JsonWriter` (manual JSON writing), not reflection-based serialization.
- **NFR-004**: The command MUST NOT write to stderr under any circumstances (errors in prompt commands cause visual artifacts in shell prompts).

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│  Shell (PowerShell / bash / zsh / fish / nushell)                    │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │ Pre-prompt hook (runs before each prompt render)               │  │
│  │   PowerShell: Set-PoshContext alias                            │  │
│  │   bash/zsh:   set_poshcontext() function                      │  │
│  │   fish:       set_poshcontext function                         │  │
│  │                                                                │  │
│  │   → runs: twig prompt                                          │  │
│  │   → sets: $env:TWIG_PROMPT / $TWIG_PROMPT                     │  │
│  └───────────────────────┬────────────────────────────────────────┘  │
│                          │ env var                                    │
│  ┌───────────────────────▼────────────────────────────────────────┐  │
│  │ Oh My Posh text segment                                        │  │
│  │   type: "text"                                                 │  │
│  │   template: "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }}  │  │
│  │              {{ end }}"                                        │  │
│  │   cache: { duration: "30s", strategy: "folder" }               │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
                          │ stdout (populating env var)
                          ▼
┌──────────────────────────────────────────────────────────────────────┐
│  twig prompt                                                         │
│  ┌──────────┐  ┌──────────────┐  ┌───────────────────────────────┐  │
│  │ DB path  │→│ .twig/ check │→│ SqliteConnection (read-only)   │  │
│  │ resolve  │  │ (fast-fail)  │  │  → context table               │  │
│  │ (w/fall- │  └──────────────┘  │  → work_items table            │  │
│  │  back)   │                    │    (includes is_dirty column)   │  │
│  └──────────┘                    └──────────────┬────────────────┘  │
│                                                  │                   │
│                                   ┌──────────────▼────────────────┐  │
│                                   │ PromptFormatter               │  │
│                                   │  (plain or JSON output)       │  │
│                                   └───────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

The `twig prompt` command uses a **fast path** that bypasses the normal DI container's lazy `SqliteCacheStore` construction. Instead, it:

1. Checks if `.twig/` exists — if not, outputs nothing and exits.
2. Uses `TwigConfiguration` (already loaded by DI at startup) for `Display.Icons`, `Display.TypeColors`, `TypeAppearances`, `Organization`, `Project`.
3. Resolves the DB path using the **same conditional logic as `Program.cs`**: if `config.Organization` and `config.Project` are both non-empty, uses `TwigPaths.ForContext()`; otherwise falls back to `Path.Combine(twigDir, "twig.db")`.
4. Checks if the resolved DB file exists — if not, outputs nothing and exits.
5. Opens a read-only SQLite connection with `busy_timeout=100`.
6. Reads the active work item ID from the `context` table.
7. Reads the work item row from the `work_items` table (single query — includes `is_dirty` column).
8. Reads `is_dirty` from the same `work_items` row (no separate query needed).
9. Reads the current git branch from `.git/HEAD` via file I/O (no subprocess) — returns null if detached HEAD or `.git/` missing.
10. Formats and outputs the result.

This fast path avoids DI container overhead, `HttpClient` construction, auth provider resolution, and all network-capable services. The dirty indicator is read directly from the `work_items.is_dirty` column (step 7), eliminating the need for a third query against `pending_changes`. This reduces SQLite operations from 3 to 2.

### Key Components

#### 1. `PromptCommand` (`src/Twig/Commands/PromptCommand.cs`)

New command class. Unlike other commands, this one does **not** depend on `IContextStore`, `IWorkItemRepository`, or `IPendingChangeStore` interfaces. Instead, it directly opens a read-only SQLite connection for maximum speed and minimal DI overhead.

```
public sealed class PromptCommand
{
    // Constructor takes only TwigConfiguration
    // Opens SQLite directly — no DI-resolved repositories
    
    public int Execute(string format = "plain", int maxWidth = 40)
    {
        // 1. Fast-fail: check .twig/ exists
        // 2. Resolve DB path (with fallback for legacy flat layout):
        //    if (config.Organization AND config.Project are non-empty)
        //      → TwigPaths.ForContext(twigDir, org, project).DbPath
        //    else
        //      → Path.Combine(twigDir, "twig.db")
        // 3. Fast-fail: check DB file exists
        // 4. Open SQLite read-only with busy_timeout=100
        // 5. Read active_work_item_id from context
        // 6. Read work item from work_items (includes is_dirty)
        // 7. Read is_dirty from same row — no pending_changes query
        // 8. Resolve git branch from .git/HEAD via file I/O (no subprocess)
        //    → if "ref: refs/heads/{branch}" → extract branch name
        //    → if raw SHA or file missing → branch = null
        // 9. Resolve type badge (unicode or nerd based on config.Display.Icons)
        // 10. Resolve type color from config.Display.TypeColors or config.TypeAppearances
        // 11. Format and write to stdout
        // 12. Return 0
    }
}
```

**Rationale for direct SQLite**: The prompt command's performance budget (<50ms) prohibits the overhead of full DI resolution, WAL mode configuration, and schema validation that `SqliteCacheStore` performs. A lightweight read-only connection with `PRAGMA query_only=ON` and `busy_timeout=100` is sufficient and faster.

**DB path fallback**: The prompt command MUST replicate the same conditional DB path resolution as `Program.cs` (lines 29-31). When `config.Organization` or `config.Project` is empty, the DB lives at `.twig/twig.db` (flat path). Calling `TwigPaths.ForContext()` unconditionally would produce a nested path that does not exist for these repositories, causing silent empty output.

#### 2. `PromptData` (internal record in PromptCommand.cs)

A lightweight data carrier for prompt output:

```csharp
internal readonly record struct PromptData(
    int Id,
    string Type,
    string TypeBadge,
    string Title,
    string State,
    string StateCategory,
    bool IsDirty,
    string? Color,
    string? Branch);
```

#### 3. `PromptBadges` (internal static class in PromptCommand.cs)

A new static utility method for badge resolution that accepts the icon mode as a parameter. This method **cannot** reuse `HumanOutputFormatter.GetTypeBadge()` because that method is `private static` and returns only Unicode glyphs — it has no nerd font branch and does not accept an icon mode parameter.

```csharp
internal static class PromptBadges
{
    internal static string GetBadge(string typeValue, string iconMode)
    {
        // Unicode badges (same as HumanOutputFormatter.GetTypeBadge):
        // ◆ (Epic), ▪ (Feature), ● (UserStory/PBI/Requirement),
        // ✦ (Bug/Impediment/Risk), □ (Task/TestCase/etc.), ■ (fallback)
        //
        // Nerd font badges (new — placeholder until IconSet is implemented):
        // Epic:    \uF0E8 (nf-fa-sitemap)
        // Feature: \uF0E7 (nf-fa-bolt)
        // Story:   \uF007 (nf-fa-user)
        // Bug:     \uF188 (nf-fa-bug)
        // Task:    \uF0AE (nf-fa-tasks)
        // Default: \uF059 (nf-fa-question_circle)
        //
        // When iconMode == "nerd", returns nerd font glyphs.
        // Otherwise returns unicode glyphs.
    }
}
```

#### 4. `GitBranchReader` (internal static class in PromptCommand.cs)

A lightweight file-based git branch reader that avoids subprocess spawning. Reads `.git/HEAD` directly to resolve the current branch name.

```csharp
internal static class GitBranchReader
{
    /// <summary>
    /// Reads the current git branch from .git/HEAD via file I/O.
    /// Returns null if: .git/ does not exist, HEAD is detached (raw SHA),
    /// HEAD file cannot be read, or any I/O error occurs.
    /// MUST NOT spawn a subprocess (e.g., git rev-parse).
    /// </summary>
    internal static string? GetCurrentBranch(string workingDirectory)
    {
        // 1. Check for .git/HEAD file
        //    Note: .git may be a file (worktrees) containing "gitdir: <path>"
        //    For v1, only handle .git/ as a directory. Worktree support deferred.
        // 2. Read first line of .git/HEAD
        // 3. If starts with "ref: refs/heads/", extract branch name after prefix
        // 4. Otherwise (detached HEAD, bare repo, etc.), return null
        // 5. Catch all IOException/UnauthorizedAccessException → return null
    }
}
```

**Rationale for file I/O**: The <50ms performance budget prohibits subprocess spawning (`git rev-parse --abbrev-ref HEAD` adds ~30-50ms of process startup). Reading `.git/HEAD` is a single file read (~0.1ms). The `.git/HEAD` file format is stable across all git versions: either `ref: refs/heads/<branch>\n` for branch tracking or a raw 40-hex-char SHA for detached HEAD.

#### 5. `OhMyPoshCommand` (`src/Twig/Commands/OhMyPoshCommand.cs`)

New command class for `twig ohmyposh init`. Generates Oh My Posh JSON snippets **and** shell hook functions.

Registered via ConsoleAppFramework v5's nested command support:

```csharp
// In Program.cs — register as nested command group:
app.Add<OhMyPoshCommands>("ohmyposh");
```

This makes `twig ohmyposh init` a valid command. The `OhMyPoshCommands` class has an `Init()` method that becomes the `init` sub-command.

#### 6. State Category Mapping

A new static method to categorize ADO states into Oh My Posh-friendly categories. The authoritative set of category values:

| State (case-insensitive) | Category | OMP Color |
|---|---|---|
| `new`, `to do`, `proposed` | `Proposed` | `#808080` (gray) |
| `active`, `doing`, `committed`, `in progress`, `approved` | `InProgress` | `#0078D4` (blue) |
| `resolved` | `Resolved` | `#0078D4` (blue) |
| `closed`, `done` | `Completed` | `#16825D` (green) |
| `removed` | `Removed` | `#808080` (gray) |
| *(unknown)* | `Unknown` | `#808080` (gray) |

**Note**: `Resolved` is treated separately from `Completed` because ADO processes (Agile, Scrum, CMMI) distinguish these states. For OMP styling, `Resolved` shares the same color as `InProgress` (blue) since it represents active work nearing completion, not yet closed. `Removed` shares color with `Proposed` (gray) since both represent inactive states.

This reuses the logic from `FormatterHelpers.GetShorthand()` but returns human-readable category names instead of shorthand codes.

### Data Flow

**`twig _prompt` (plain format):**

```
1. Check: Directory.Exists(.twig/) → false → write "" to stdout, return 0
2. Use: config.Display.Icons, config.Display.TypeColors, config.TypeAppearances, config.Organization, config.Project
   (provided via constructor — TwigConfiguration is already loaded by DI at startup per Program.cs lines 23-25)
3. Resolve DB path:
   if (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
     → dbPath = TwigPaths.GetContextDbPath(twigDir, config.Organization, config.Project)
   else
     → dbPath = Path.Combine(twigDir, "twig.db")
4. Check: File.Exists(dbPath) → false → write "" to stdout, return 0
5. Open: SqliteConnection($"Data Source={dbPath};Mode=ReadOnly", busy_timeout=100)
6. Query: SELECT value FROM context WHERE key = 'active_work_item_id'
   → null → close, write "" to stdout, return 0
7. Query: SELECT id, type, title, state, is_dirty FROM work_items WHERE id = @id
   → null → close, write "" to stdout, return 0
   → read is_dirty from this row (0 or 1)
8. Resolve branch: read .git/HEAD via file I/O (no subprocess)
   → if file starts with "ref: refs/heads/", extract branch name
   → if file is a raw SHA (detached HEAD) or missing, branch = null
9. Resolve badge: PromptBadges.GetBadge(type, config.Display.Icons)
10. Resolve color: config.Display.TypeColors?[type] ?? config.TypeAppearances?.Find(t => t.Name == type)?.Color
11. Format: "{badge} #{id} {truncated_title} [{state}]{dirty}"
12. Write to stdout, close, return 0
```

**`twig _prompt --format json`:**

Same data flow as above, but step 11 outputs a JSON object using `Utf8JsonWriter`:

```json
{"id":12345,"type":"Epic","typeBadge":"◆","title":"Implement login flow","state":"Active","stateCategory":"InProgress","isDirty":true,"color":"#8B00FF","branch":"feature/login"}
```

### API Contracts

#### CLI: `twig _prompt`

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--format` | `string` | `"plain"` | Output format: `plain` or `json` |
| `--max-width` | `int` | `40` | Maximum title width before truncation |

**Plain output format:**
```
◆ #12345 Implement login flow [Active] •
```

**JSON output format:**
```json
{"id":12345,"type":"Epic","typeBadge":"◆","title":"Implement login flow","state":"Active","stateCategory":"InProgress","isDirty":true,"color":"#8B00FF","branch":"feature/login"}
```

#### CLI: `twig ohmyposh init`

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--style` | `string` | `"powerline"` | Segment style: `powerline`, `plain`, or `diamond` |
| `--shell` | `string` | `"pwsh"` | Shell for the hook function: `pwsh`, `bash`, `zsh`, `fish` |

**Output**: Two sections — (1) a shell hook function to add to the user's shell profile, and (2) a JSON snippet for an Oh My Posh `text` segment block, ready to paste into the user's `.omp.json` / `.omp.yaml` / `.omp.toml`.

**ConsoleAppFramework v5 routing**: The `OhMyPoshCommands` class is registered as a nested command group via `app.Add<OhMyPoshCommands>("ohmyposh")`. This means the `Init()` public method becomes the `twig ohmyposh init` command. This follows ConsoleAppFramework v5's documented nested command pattern (see [Nested command](https://github.com/Cysharp/ConsoleAppFramework#nested-command)).

### Design Decisions

#### RD-001: Environment variable + `text` segment (selected) vs. other approaches

| Approach | Pros | Cons |
|---|---|---|
| **Environment variable + `text` segment** (selected) | Cross-shell (PowerShell, bash, zsh, fish, nushell); no subprocess at OMP render time (reads env var only); uses documented `Set-PoshContext` / `set_poshcontext` hook mechanism; simple `text` segment config; user controls hook execution timing | Requires user to add hook function to shell profile; subprocess runs in pre-prompt hook (before OMP renders); env var stale until next prompt |
| Native Go segment | Structured template variables (`.Id`, `.Type`); no subprocess overhead; first-class OMP integration | Requires PR to Oh My Posh repo; ongoing maintenance burden; Go dependency; release coupling |

**Decision**: Use the environment variable + `text` segment approach. This is the **only** viable approach for running external commands per-prompt in Oh My Posh. The `command` segment type does not exist in Oh My Posh (verified against OMP source and JSON schema). The env var pattern is cross-shell, documented for PowerShell/Zsh/Bash/Fish/Nushell via the `Set-PoshContext` / `set_poshcontext` hook mechanism (see [OMP Templates — Environment variables](https://ohmyposh.dev/docs/configuration/templates#environment-variables)). The subprocess overhead is incurred in the pre-prompt hook (not in OMP rendering), and the hook runs `twig prompt` which completes in <50ms. **Note**: The Claude Code OMP segment (`type: "claude"`) is **not** an example of this pattern — it is a native Go segment compiled into OMP, which reads `POSH_CLAUDE_STATUS` internally. The env var + text segment approach chosen here does not have an OMP-native equivalent precedent; it relies on the generic `Set-PoshContext` hook, which is a documented first-party OMP feature.

#### RD-002: Direct SQLite vs. DI-resolved repositories

**Decision**: Use direct SQLite queries in `PromptCommand`. The normal code path through `SqliteCacheStore` → `SqliteContextStore` → `SqliteWorkItemRepository` adds overhead from: DI resolution, WAL mode pragma execution, schema version validation, and connection setup for write-capable mode. The prompt command only needs two read queries. A direct `SqliteConnection` with `Mode=ReadOnly` and `busy_timeout=100` is simpler and faster.

#### RD-003: Synchronous vs. async execution

**Decision**: Use synchronous SQLite access. The prompt command runs two sequential queries against a local file. `async` overhead (state machine allocation, Task scheduling) is measurable in a <50ms budget. `contoso.Data.Sqlite` supports synchronous `ExecuteScalar()` and `ExecuteReader()` natively.

#### RD-004: Error handling — silent failure

**Decision**: The prompt command MUST never write to stderr and MUST never throw. Any error (missing directory, locked DB, corrupt DB, missing work item) results in empty stdout and exit code 0. Shell prompts break visibly when a segment command writes errors or returns non-zero.

#### RD-005: Title truncation

**Decision**: Truncate at `--max-width` characters (default 40). If the title exceeds this length, truncate at `max-width - 1` characters and append `…` (U+2026 HORIZONTAL ELLIPSIS). This keeps the prompt segment compact on standard 80-column terminals.

#### RD-006: Dirty detection from `is_dirty` column (not `pending_changes`)

**Decision**: Read `is_dirty` directly from the `work_items` row returned by the work item query (step 7 in data flow). The `work_items` table DDL includes `is_dirty INTEGER NOT NULL DEFAULT 0`, and this field is maintained in sync by `SqliteWorkItemRepository.SaveAsync()` via the `@isDirty` parameter. This eliminates the need for a third SQLite query against `pending_changes`, reducing I/O from 3 queries to 2, improving both performance and reliability.

#### RD-007: DB path resolution with legacy fallback

**Decision**: The prompt command MUST replicate the conditional DB path logic from `Program.cs` (lines 29-31). If both `config.Organization` and `config.Project` are non-empty, use `TwigPaths.GetContextDbPath(twigDir, org, project)`. Otherwise, fall back to `Path.Combine(twigDir, "twig.db")`. This ensures the prompt works for repositories using the legacy flat layout (before multi-context support) as well as the current nested layout.

#### RD-008: Type badge — new static utility (not reuse of GetTypeBadge)

**Decision**: Create a new `PromptBadges.GetBadge(string typeValue, string iconMode)` static method rather than reusing `HumanOutputFormatter.GetTypeBadge()`. The existing method is `private static`, returns only Unicode glyphs unconditionally, does not accept an icon mode parameter, and does not reference `display.icons`. The nerd font `IconSet` class from the prior nerd-font-icons plan has not been implemented. The new method duplicates the Unicode switch from `GetTypeBadge()` and adds a nerd font branch gated on `iconMode == "nerd"`. When/if `IconSet` is implemented in the future, `PromptBadges` can delegate to it.

#### RD-009: `resolved` state color — blue in prompt (differs from green in `twig status`)

**Decision**: The prompt segment maps `resolved` to the `Resolved` state category with blue (`#0078D4`) coloring. This intentionally differs from `HumanOutputFormatter.GetStateColor()`, which maps `resolved` to Green (line 258: `"closed" or "done" or "resolved" => Green`). The rationale: `twig status` uses ANSI color codes for terminal display where resolved/closed/done are visually grouped as "terminal states" (green). The prompt segment uses a separate ADO-aligned state category model where `Resolved` ≠ `Completed` — in ADO processes (Agile, Scrum, CMMI), `Resolved` means "fix applied, awaiting verification," which is semantically closer to `InProgress` than `Completed`. Users may see different colors for the same state across `twig status` (green) and the prompt segment (blue). This is accepted as correct domain modeling. If user feedback indicates this causes confusion, a future option (e.g., `display.prompt.resolvedColor`) can unify the behavior.

### Oh My Posh Segment Configuration

#### Shell Hook Functions

**PowerShell** (add to `$PROFILE`):
```powershell
function Set-TwigPrompt {
    $env:TWIG_PROMPT = (twig _prompt 2>$null)
}
New-Alias -Name 'Set-PoshContext' -Value 'Set-TwigPrompt' -Scope Global -Force
```

**Bash** (add to `~/.bashrc` after `eval "$(oh-my-posh init bash)"`):
```bash
set_poshcontext() {
    export TWIG_PROMPT="$(twig _prompt 2>/dev/null)"
}
```

**Zsh** (add to `~/.zshrc` after `eval "$(oh-my-posh init zsh)"`):
```zsh
set_poshcontext() {
    export TWIG_PROMPT="$(twig _prompt 2>/dev/null)"
}
```

**Fish** (add to `~/.config/fish/config.fish` after OMP init):
```fish
function set_poshcontext
    set -gx TWIG_PROMPT (twig _prompt 2>/dev/null)
end
```

#### Powerline style (recommended)

```json
{
  "type": "text",
  "style": "powerline",
  "powerline_symbol": "\uE0B0",
  "foreground": "#ffffff",
  "background": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

#### Plain style

```json
{
  "type": "text",
  "style": "plain",
  "foreground": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

#### Diamond style

```json
{
  "type": "text",
  "style": "diamond",
  "leading_diamond": "\uE0B6",
  "trailing_diamond": "\uE0B4",
  "foreground": "#ffffff",
  "background": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

#### Caching strategy

Oh My Posh's `cache` property is optional for `text` segments reading environment variables. The env var is set by the shell hook before each prompt render, so OMP always reads the current value. However, adding a cache can reduce the visual overhead of evaluating the template:

- **`strategy: "folder"`** — caches per working directory. The cache is invalidated when the user changes directories. This is correct because `.twig/` context is per-repository.
- **`duration: "30s"`** — cache the segment result for 30 seconds. This is a reasonable tradeoff: changes from `twig set` or `twig state` appear within 30 seconds, while reducing template evaluation overhead. The `5s` value from v1 was technically valid but shorter than optimal for an env-var-based segment.

---

## Alternatives Considered

### Alternative: Native Go segment in Oh My Posh

A custom Go segment could be contributed to the Oh My Posh repository, providing structured template variables (`.Id`, `.Type`, `.TypeBadge`, `.State`, `.StateCategory`, `.IsDirty`) and first-class integration with OMP's color template system.

**Pros**: Structured template variables enable dynamic `foreground_templates`/`background_templates` based on state category. No subprocess or env var needed. True first-class integration.
**Cons**: Requires a PR to the Oh My Posh repository and ongoing maintenance. Couples Twig's release cycle to OMP's. Requires Go toolchain. The Claude Code segment is an example of this native Go approach (`type: "claude"`, compiled into OMP binary, reads `POSH_CLAUDE_STATUS`), but Claude Code has significantly more users to justify the maintenance burden.

**Decision**: Deferred (NG-1). The env var approach provides the core functionality with zero upstream dependency. If demand grows, a native segment can be proposed later.

### Alternative: JSON format + dynamic template colors via env var

With the env var approach, `twig prompt --format json` could output structured JSON to `TWIG_PROMPT_JSON`, and a more complex OMP template could parse individual fields using `{{ fromJson .Env.TWIG_PROMPT_JSON }}` via sprig's `fromJson` function. This would enable dynamic background colors based on state category.

**Pros**: Dynamic segment colors (e.g., green background for completed, blue for in-progress). Richer template customization.
**Cons**: Complex template syntax. OMP's `fromJson` support in templates is not well-documented for this use case. Adds fragility. Most users prefer the simple pre-formatted text approach.

**Decision**: Document as an advanced alternative. The default plain-text approach with static colors covers >90% of use cases. Power users can use `--format json` and custom templates.

---

## Dependencies

### External dependencies

| Dependency | Version | Purpose |
|---|---|---|
| Oh My Posh | v19+ | `text` segment type, `cache` property, `Set-PoshContext` / `set_poshcontext` hook support |
| contoso.Data.Sqlite | (existing) | SQLite access for prompt queries |
| SQLitePCLRaw.bundle_e_sqlite3 | (existing) | SQLite native library |
| ConsoleAppFramework | v5.7.13 (existing) | CLI command routing, including nested commands via `app.Add<T>("commandPath")` |

### Internal dependencies

| Component | Dependency |
|---|---|
| `PromptCommand` | `TwigConfiguration` (for `Display.Icons`, `Display.TypeColors`, `TypeAppearances`, `Organization`, `Project`) |
| `PromptCommand` | `PromptBadges.GetBadge()` — new static utility method (cannot reuse `HumanOutputFormatter.GetTypeBadge()` — it is `private static`, Unicode-only, no icon mode parameter) |
| `OhMyPoshCommands` | None (pure output generation) |

### Sequencing constraints

- The `display.icons` config key exists — no prerequisites.
- The `display.typeColors` config exists — no prerequisites.
- The nerd font `IconSet` class has **not** been implemented. `PromptBadges` will provide placeholder nerd font glyphs using standard Nerd Font codepoints. When `IconSet` is implemented (per the nerd-font-icons plan), `PromptBadges` can be updated to delegate to it.

---

## Impact Analysis

### Components affected

| Component | Impact |
|---|---|
| `Program.cs` | Add `Prompt()` method to `TwigCommands` (flat command). Register `PromptCommand` in DI. Register `OhMyPoshCommands` as nested command group via `app.Add<OhMyPoshCommands>("ohmyposh")`. |
| `TwigJsonContext` | Add `[JsonSerializable]` for `PromptData` (if JSON format used). (May not be needed if using `Utf8JsonWriter` directly.) |

### Backward compatibility

- No existing commands are modified.
- No existing config keys are changed.
- No schema changes to the SQLite database.
- The `.twig/config` file is not modified by any new command.
- The prompt command correctly handles both legacy flat DB layout (`.twig/twig.db`) and multi-context nested layout (`.twig/{org}/{project}/twig.db`).

### Performance implications

- `twig prompt` is a new command — no impact on existing commands.
- The env var approach means Oh My Posh reads an environment variable (zero cost), not a subprocess. The subprocess runs in the shell's pre-prompt hook, before OMP rendering.
- Direct SQLite read with `busy_timeout=100` means the prompt command will not block if another `twig` process holds a write lock — it will return empty output after 100ms.
- Only 2 SQLite queries required (context + work_items), not 3 — dirty indicator comes from `work_items.is_dirty` column.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `twig prompt` exceeds 50ms on cold start | Medium | Medium | Benchmark AOT binary on Windows/macOS/Linux. The .NET AOT startup time is typically 5-15ms. SQLite open + 2 queries should be <15ms. Total <30ms expected. |
| Shell hook function not added by user correctly | Medium | Low | `twig ohmyposh init` outputs the exact hook function with copy-paste-ready syntax for each shell. Documentation covers common pitfalls. |
| SQLite `busy_timeout=100` is too short, causing frequent empty prompts | Low | Low | The prompt command only needs read access — SQLite WAL mode allows concurrent reads even during writes. The 100ms timeout is a safety net, not the primary mechanism. |
| Users on Windows with slow antivirus scanning `.twig/twig.db` | Medium | Low | AOT binary startup is fast. DB access is via already-opened file handles. The env var approach means `twig prompt` runs once per prompt (in the hook), not on every OMP render. |
| Oh My Posh changes `Set-PoshContext` / `set_poshcontext` hook mechanism | Low | High | The hook mechanism is stable and documented as a first-party OMP feature (see [Templates — Environment variables](https://ohmyposh.dev/docs/configuration/templates#environment-variables)). Pin documentation to specific OMP version. |
| Legacy flat DB path not found by prompt command | Low (fixed) | High | Addressed by RD-007: the prompt command replicates the same conditional DB path resolution as `Program.cs`, supporting both flat and nested layouts. |

---

## Open Questions

1. **Q-001**: Should `twig prompt` support a `--timeout` flag to override the SQLite `busy_timeout`? The 100ms default is aggressive — some environments with slow I/O may need more time.

2. **Q-002**: Should the plain format include ANSI color codes? The env var approach passes the string through an environment variable, and OMP's `text` segment renders it literally. ANSI codes in the env var would provide inline coloring. However, this may conflict with Oh My Posh's own foreground/background coloring. **Preliminary decision**: No ANSI codes in plain format — let Oh My Posh control colors via segment `foreground`/`background`.

3. **Q-003**: Should `twig ohmyposh init` support YAML and TOML output in addition to JSON? Oh My Posh supports all three config formats. **Preliminary decision**: JSON only for v1 — it's the most common and the default.

4. **Q-004**: Should the hook function include error suppression beyond `2>/dev/null`? For example, should it check if `twig` is on PATH before running? **Preliminary decision**: No — if `twig` is not on PATH, the hook silently produces an empty env var (the `2>/dev/null` / `2>$null` suppresses the error), and the OMP segment shows nothing. This is the desired graceful degradation behavior.

---

## Implementation Phases

### Phase 1: `twig prompt` command (core)
**Exit criteria**: `twig prompt` outputs correct plain-text and JSON summaries of the active work item, completes in <50ms, handles all graceful degradation cases, and correctly resolves DB path for both legacy flat and multi-context nested layouts.

### Phase 2: `twig ohmyposh init` helper
**Exit criteria**: `twig ohmyposh init` outputs valid shell hook functions and Oh My Posh `text` segment JSON snippets for all supported shells and styles.

### Phase 3: Documentation and samples
**Exit criteria**: README section, sample Oh My Posh configs, and shell setup instructions for PowerShell, bash, zsh, and fish.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/PromptCommand.cs` | `twig prompt` command — reads SQLite directly, outputs compact work item summary. Includes `PromptData` record, `PromptBadges` static class, and state category mapping. |
| `src/Twig/Commands/OhMyPoshCommands.cs` | `twig ohmyposh init` command — generates shell hook functions and Oh My Posh `text` segment JSON snippets |
| `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | Unit tests for `PromptCommand` |
| `tests/Twig.Cli.Tests/Commands/OhMyPoshCommandsTests.cs` | Unit tests for `OhMyPoshCommands` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Add `Prompt()` method to `TwigCommands` (flat command, registered via existing `app.Add<TwigCommands>()`). Register `PromptCommand` in DI. Add `app.Add<OhMyPoshCommands>("ohmyposh")` for nested command group routing. Register `OhMyPoshCommands` in DI. |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### EPIC-001: `twig _prompt` command

**Goal**: Implement the core `twig _prompt` hidden command that reads from local SQLite and outputs a compact work item summary. The command is hidden from `--help` output using ConsoleAppFramework's `[Hidden]` attribute and the `_` prefix naming convention (matching `_hook`).

**Prerequisites**: None — all infrastructure dependencies already exist.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `PromptCommand` class with direct SQLite access. Implement fast-path: check `.twig/` exists → use DI-injected config → resolve DB path **with legacy flat fallback** (replicate `Program.cs` lines 29-31: if `config.Organization` AND `config.Project` are non-empty → `TwigPaths.GetContextDbPath()`; else → `Path.Combine(twigDir, "twig.db")`) → check DB file exists → open read-only connection (`Mode=ReadOnly`, `busy_timeout=100`) → query context table → query work_items table (SELECT id, type, title, state, is_dirty) → read `is_dirty` from same row (**no** `pending_changes` query) → resolve git branch from `.git/HEAD` via file I/O → format output. Handle all graceful degradation cases (missing dir, missing DB, no active item, missing work item, locked DB). | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-002 | IMPL | Add state category mapping — static method `GetStateCategory(string state)` that maps ADO state strings to categories: `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`. Reuse the same switch expression pattern as `FormatterHelpers.GetShorthand()` but with human-readable category names. Authoritative category values: `Proposed` (new/to do/proposed), `InProgress` (active/doing/committed/in progress/approved), `Resolved` (resolved), `Completed` (closed/done), `Removed` (removed), `Unknown` (fallback). Place in `PromptCommand.cs`. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-003 | IMPL | Create new `PromptBadges.GetBadge(string typeValue, string iconMode)` static utility method. **Cannot** reuse `HumanOutputFormatter.GetTypeBadge()` — it is `private static`, returns only Unicode glyphs (◆▪●✦□■), does not accept an icon mode parameter, and does not reference `display.icons`. The new method duplicates the Unicode switch and adds a nerd font branch: when `iconMode == "nerd"`, returns Nerd Font codepoints (e.g., `\uF0E8` sitemap for Epic, `\uF0E7` bolt for Feature, `\uF007` user for Story, `\uF188` bug for Bug, `\uF0AE` tasks for Task). When `iconMode != "nerd"`, returns Unicode glyphs matching `GetTypeBadge()`. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-003a | IMPL | Create `GitBranchReader.GetCurrentBranch(string workingDirectory)` static method. Read `.git/HEAD` via `File.ReadAllText()` — if starts with `ref: refs/heads/`, extract branch name. Return null for detached HEAD, missing `.git/`, or any `IOException`/`UnauthorizedAccessException`. MUST NOT spawn subprocess. Git worktree support (`.git` as file) deferred. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-004 | IMPL | Add title truncation — static method `TruncateTitle(string title, int maxWidth)` that truncates at `maxWidth - 1` and appends `…`. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-005 | IMPL | Implement JSON output format using `Utf8JsonWriter` (AOT-compatible, no reflection). Output keys: `id`, `type`, `typeBadge`, `title`, `state`, `stateCategory`, `isDirty`, `color`, `branch`. Color resolution: check `config.Display.TypeColors?[type]` first, then fall back to `config.TypeAppearances?.Find(t => t.Name == type)?.Color` (note: `TypeAppearances` is a top-level property on `TwigConfiguration`, **not** nested under `Display`). Branch resolution: `GitBranchReader.GetCurrentBranch()`. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-006 | IMPL | Register `PromptCommand` in DI container and add `Prompt()` method to `TwigCommands` in `Program.cs` with `[Hidden]` and `[Command("_prompt")]` attributes (matching `_hook` convention). Wire `--format` and `--max-width` parameters. | `src/Twig/Program.cs` | DONE |
| ITEM-007 | TEST | Unit tests for `PromptCommand`: (a) returns empty when `.twig/` missing, (b) returns empty when no active item, (c) returns correct plain format, (d) returns correct JSON format with `branch` field, (e) title truncation at boundary, (f) dirty indicator present/absent (read from `is_dirty` column, not `pending_changes`), (g) nerd font badge when `display.icons=nerd`, (h) type color included in JSON when `display.typeColors` configured, (i) legacy flat DB path resolution when `config.Organization` is empty, (j) nested DB path resolution when `config.Organization` and `config.Project` are set, (k) branch is null when `.git/HEAD` is detached, (l) branch correctly extracted from `.git/HEAD` symbolic ref. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-008 | TEST | Unit tests for state category mapping (`GetStateCategory`), title truncation (`TruncateTitle`), and `GitBranchReader.GetCurrentBranch()` helpers. Include all 6 category values: Proposed, InProgress, Resolved, Completed, Removed, Unknown. Git branch tests: symbolic ref parsing, detached HEAD, missing .git/, I/O error handling. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |

**Acceptance Criteria**:

- [x] `twig _prompt` outputs `◆ #12345 Implement login flow… [Active] •` for an active dirty Epic work item
- [x] `twig _prompt` outputs empty string when `.twig/` does not exist
- [x] `twig _prompt` outputs empty string when no active work item is set
- [x] `twig _prompt --format json` outputs valid JSON with all specified keys including `stateCategory`, `branch`
- [x] `twig _prompt --format json` includes `branch` with correct value when on a git branch
- [x] `twig _prompt --format json` includes `branch: null` when HEAD is detached or `.git/` is missing
- [x] `twig _prompt --max-width 20` truncates the title correctly
- [x] `twig _prompt` works with legacy flat DB path (`.twig/twig.db`) when `config.Organization` is empty
- [x] `twig _prompt` works with nested DB path (`.twig/{org}/{project}/twig.db`) when both `config.Organization` and `config.Project` are set
- [x] `twig _prompt` is hidden from `--help` output via `[Hidden]` attribute
- [x] Dirty indicator reads from `work_items.is_dirty` column (only 2 SQLite queries, not 3)
- [x] All tests pass
- [x] AOT build succeeds

---

### EPIC-002: `twig ohmyposh init` helper

**Goal**: Implement the `twig ohmyposh init` command that generates shell hook functions and Oh My Posh `text` segment JSON snippets.

**Prerequisites**: EPIC-001 (the snippet references `twig prompt`).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-009 | IMPL | Create `OhMyPoshCommands` class with `Init()` method. Generate two outputs: (1) Shell hook function for the specified shell (`--shell pwsh\|bash\|zsh\|fish`): PowerShell uses `Set-PoshContext` alias pattern calling `twig _prompt`, bash/zsh uses `set_poshcontext()` function calling `twig _prompt`, fish uses `set_poshcontext` function calling `twig _prompt`. (2) Oh My Posh `text` segment JSON with `type: "text"`, specified style, colors, and `template: "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}"`. Use `Utf8JsonWriter` with `Indented = true` for readable output. | `src/Twig/Commands/OhMyPoshCommands.cs` | DONE |
| ITEM-010 | IMPL | Register `OhMyPoshCommands` as a nested command group in `Program.cs` using ConsoleAppFramework v5's `app.Add<OhMyPoshCommands>("ohmyposh")` pattern. This makes `Init()` accessible as `twig ohmyposh init`. Register in DI: `services.AddSingleton<OhMyPoshCommands>()`. Wire `--style` and `--shell` parameters on `Init()`. | `src/Twig/Program.cs` | DONE |
| ITEM-011 | TEST | Unit tests for `OhMyPoshCommands`: (a) powerline style output contains valid JSON with `"type": "text"`, (b) plain style output, (c) diamond style output, (d) PowerShell hook contains `Set-PoshContext`, (e) bash hook contains `set_poshcontext()`, (f) fish hook uses `set -gx TWIG_PROMPT`, (g) template contains `{{ .Env.TWIG_PROMPT }}`, (h) no `"type": "command"` anywhere in output. | `tests/Twig.Cli.Tests/Commands/OhMyPoshCommandsTests.cs` | DONE |

**Acceptance Criteria**:

- [x] `twig ohmyposh init` outputs a valid shell hook function and Oh My Posh `text` segment JSON
- [x] `twig ohmyposh init --style plain` outputs plain style configuration
- [x] `twig ohmyposh init --style diamond` outputs diamond style configuration
- [x] `twig ohmyposh init --shell pwsh` outputs PowerShell `Set-PoshContext` alias
- [x] `twig ohmyposh init --shell bash` outputs bash `set_poshcontext()` function
- [x] `twig ohmyposh init --shell fish` outputs fish `set_poshcontext` function
- [x] Output JSON uses `"type": "text"` (not `"type": "command"`)
- [x] Output JSON is valid and parseable
- [x] All tests pass

---

### EPIC-003: Documentation

**Goal**: Document the Oh My Posh integration for end users.

**Prerequisites**: EPIC-001 and EPIC-002.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Add "Oh My Posh Integration" section to project documentation. Include: overview, quick start (3 steps: add hook to shell profile, add segment to OMP config, restart shell), sample configs for all three styles, PowerShell setup, bash/zsh setup, fish setup, troubleshooting. Explicitly state that the integration uses the environment variable + `text` segment pattern (not a `command` segment). | `docs/ohmyposh.md` | DONE |
| ITEM-013 | IMPL | Add example Oh My Posh theme file with Twig `text` segment pre-configured alongside common segments (path, git, time). Include the shell hook function comments. | `docs/examples/twig.omp.json` | DONE |

**Acceptance Criteria**:

- [x] Documentation covers PowerShell, bash, zsh, and fish setup
- [x] Sample Oh My Posh theme file is valid JSON with `"type": "text"` segments
- [x] Quick start guide is 3 steps or fewer
- [x] Troubleshooting section covers: segment not showing, stale data, performance
- [x] Documentation clearly explains the env var + hook pattern

---

## References

- [Oh My Posh — Segment Configuration](https://ohmyposh.dev/docs/configuration/segment) — segment types, styles, caching, include/exclude folders. **Note**: Valid segment types are listed here; `command` is NOT among them.
- [Oh My Posh — Templates](https://ohmyposh.dev/docs/configuration/templates) — Go template syntax, global properties, `{{ .Env.VarName }}` access, cross-segment references, `Set-PoshContext` / `set_poshcontext` hook documentation (PowerShell, Zsh, Bash, Fish, Nushell)
- [Oh My Posh — Text Segment](https://ohmyposh.dev/docs/segments/system/text) — `text` segment type used for displaying env var content
- [Oh My Posh — Colors](https://ohmyposh.dev/docs/configuration/colors) — hex colors, palettes, color templates, inline overrides
- [Oh My Posh — Claude Code Segment](https://ohmyposh.dev/docs/segments/cli/claude) — **native Go segment** (`type: "claude"`) compiled into OMP binary. Reads `POSH_CLAUDE_STATUS` env var internally. **Not** an example of the env var + text segment pattern — included as reference for the alternative approach (NG-1) and for Claude Code's `oh-my-posh claude` CLI integration.
- [Oh My Posh — Add Segment (Contributing)](https://ohmyposh.dev/docs/contributing/segment) — native Go segment development (reference for NG-1 deferral)
- [ConsoleAppFramework v5 Documentation](https://github.com/Cysharp/ConsoleAppFramework) — CLI framework used by Twig. See "Nested command" section for `app.Add<T>("commandPath")` pattern.
- [Twig Nerd Font Icons Plan](docs/projects/twig-nerd-font-icons.plan.md) — `display.icons` config and `IconSet` implementation (not yet implemented)
- [contoso.Data.Sqlite Documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) — SQLite library used by Twig
