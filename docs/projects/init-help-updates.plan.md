# Init & Help Updates — Solution Design

| Field | Value |
|-------|-------|
| **Work Item** | #1952 (Issue) |
| **Author** | Copilot |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan enhances the `twig init` wizard and the `twig --help` grouped help system
to support workspace modes and improve command discoverability. The init wizard gains
two new behaviours: (1) an interactive prompt asking the user for a default workspace
mode (sprint or workspace, defaulting to sprint), persisted as `defaults.mode` in the
config file, and (2) a warning when `.twig` is created outside a `.git` root directory.
The help system gains two new categories — **Workspace** (grouping `workspace`, `ws`,
`sprint`, and `area` commands) and **Seeds** (grouping all `seed *` commands) — pulling
these commands out of the existing monolithic Views and Work Items sections. Together
these changes streamline onboarding and reduce cognitive load when reading `twig --help`.

## Background

### Current Init Wizard

`InitCommand.ExecuteAsync` (src/Twig/Commands/InitCommand.cs) runs a linear wizard:
1. Creates `.twig/` directory and nested `{org}/{project}/` context directory
2. Writes a `TwigConfiguration` JSON config file at `.twig/config`
3. Detects process template, type appearances, area paths, current iteration, user identity
4. Syncs type state sequences, process configuration, and field definitions
5. Applies global profile status-fields (if available)
6. Appends `.twig/` to `.gitignore` (SEC-001)
7. Populates cache with current sprint items

The wizard has **no interactive prompts** today — all parameters come from CLI arguments.
There is no detection of whether the CWD is a git repository root.

### Current Help System

`GroupedHelp` (src/Twig/Program.cs, line 943) renders a manually-maintained grouped
help block with these categories:
- **Getting Started** — `init`, `sync`
- **Views** — `status`, `tree`, `workspace`, `sprint`
- **Context** — `set`, `show`, `show-batch`, `query`, `web`
- **Navigation** — `nav *`
- **Work Items** — `state`, `states`, `batch`, `note`, `update`, `edit`, `new`, `link *`,
  `seed *`, `discard`, `sync`
- **Git** — `branch`, `commit`, `pr`, `stash *`, `log`, `context`, `hooks *`
- **Workflow** — `flow-start`, `flow-done`, `flow-close`
- **System** — `config *`, `version`, `upgrade`, `changelog`
- **Experimental** — `tui`, `mcp`, `ohmyposh init`

The Work Items category is oversized (20+ entries). Seeds and workspace views
are buried in larger categories.

`GroupedHelp.KnownCommands` (HashSet) contains every accepted command token and is
validated by `GroupedHelpTests.AllNonHiddenCommands_AppearInGroupedHelp` (T-1523-4).

`CommandExamples` (src/Twig/CommandExamples.cs) maps command names to example arrays.
Every non-hidden command must have ≥2 examples (enforced by
`GroupedHelpTests.AllCommands_HaveExamples`).

### Configuration Model

`TwigConfiguration` (src/Twig/Infrastructure/Config/TwigConfiguration.cs) is a POCO
serialized via `TwigJsonContext.Default.TwigConfiguration`. It contains nested config
objects: `Auth`, `Defaults`, `Seed`, `Display`, `User`, `Git`, `Flow`.
`TwigConfiguration.SetValue` handles dot-path config updates reflection-free.

### Call-Site Audit

The following cross-cutting components are modified by this plan:

| File | Component | Current Usage | Impact |
|------|-----------|---------------|--------|
| `src/Twig/Program.cs` | `GroupedHelp.Show()` | Renders all help categories | Add Workspace & Seeds categories, move commands |
| `src/Twig/Program.cs` | `GroupedHelp.KnownCommands` | Lists all known command tokens | Add `area` if it becomes a real command |
| `src/Twig/CommandExamples.cs` | `Examples` dictionary | Per-command usage examples | No changes needed — existing entries remain valid |
| `src/Twig/Commands/InitCommand.cs` | `ExecuteCoreAsync` | Linear init wizard | Add workspace mode prompt + git root warning |
| `src/Twig/Infrastructure/Config/TwigConfiguration.cs` | `DefaultsConfig` | Area path, iteration path | Add `Mode` property |
| `src/Twig/Infrastructure/Config/TwigConfiguration.cs` | `SetValue()` | Handles `defaults.areapath` etc. | Add `defaults.mode` case |
| `src/Twig/Infrastructure/Serialization/TwigJsonContext.cs` | Source-gen context | Serializes `TwigConfiguration` | No new types needed (Mode is a string on existing class) |
| `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` | Completeness tests | Validates all commands appear | Update expected categories |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Init unit tests | Tests init wizard flow | Add tests for mode prompt + git warning |

## Problem Statement

1. **No workspace mode selection during init.** Users cannot express whether they prefer
   sprint-scoped or workspace-scoped views as their default during onboarding. The mode
   must be changed post-init via `twig config`.

2. **Accidental init outside git root.** Running `twig init` in a subdirectory or
   non-repo directory creates a `.twig` directory that may be unintended. There is no
   guardrail or warning.

3. **Help output is hard to scan.** The Work Items category has 20+ entries mixing
   seed commands, link commands, and core editing commands. Workspace-related views
   (`workspace`, `ws`, `sprint`) are in the generic Views category with no callout.
   Users don't discover seed or workspace commands easily.

## Goals and Non-Goals

### Goals

1. `twig init` prompts for default workspace mode (sprint/workspace), defaulting to sprint
2. `twig init` warns when `.twig` is created outside a `.git` root, with a continue/abort prompt
3. The mode preference is persisted in `.twig/config` as `defaults.mode`
4. `twig --help` shows a new **Workspace** category grouping `workspace`, `ws`, `sprint`
5. `twig --help` shows a new **Seeds** category grouping all `seed *` commands
6. Existing test suite passes with updated category expectations
7. All commands continue to appear in exactly one help category

### Non-Goals

- Adding a new standalone `area` command (the scope says to group `area` but no `area`
  command exists today — this plan groups only existing commands)
- Changing default behavior of `workspace` or `sprint` commands based on mode (deferred)
- Adding Spectre.Console interactive prompts (keep using `IConsoleInput.ReadLine`)
- Persisting mode in the SQLite database (config file is sufficient)

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | During `twig init`, after config is written but before ADO sync, prompt: "Default workspace mode? [sprint/workspace] (sprint):" |
| FR-2 | Accept `sprint` or `workspace` as valid mode values; any other input re-prompts (max 3 attempts, then default) |
| FR-3 | Persist the chosen mode as `defaults.mode` in `.twig/config` |
| FR-4 | Before creating `.twig/`, check if a `.git` directory exists alongside the target location. If not, warn: "⚠ No .git directory found at {path}. This may not be a repository root. Continue? [y/N]:" |
| FR-5 | If the user declines (N/Enter), abort init with exit code 1 |
| FR-6 | `twig config defaults.mode` reads/writes the mode value |
| FR-7 | `twig --help` shows a "Workspace:" category containing `workspace`, `ws`, `sprint` |
| FR-8 | `twig --help` shows a "Seeds:" category containing all `seed *` commands |
| FR-9 | Commands in Workspace/Seeds categories are removed from their current Views/Work Items categories |
| FR-10 | Skip interactive prompts when output is redirected (non-TTY) — use defaults silently |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF-1 | No new NuGet dependencies |
| NF-2 | All prompts use `IConsoleInput` for testability |
| NF-3 | AOT-compatible — no reflection, no dynamic type loading |
| NF-4 | `TreatWarningsAsErrors` must remain green |

## Proposed Design

### Architecture Overview

The changes are localized to two areas:

```
┌─────────────────────────────────┐
│           CLI Layer             │
│  ┌───────────┐  ┌────────────┐  │
│  │InitCommand│  │GroupedHelp  │  │
│  │  + mode   │  │  + new     │  │
│  │  prompt   │  │  categories│  │
│  │  + git    │  │            │  │
│  │  warning  │  │            │  │
│  └─────┬─────┘  └────────────┘  │
│        │                        │
│  ┌─────▼─────────────────────┐  │
│  │   TwigConfiguration       │  │
│  │   DefaultsConfig.Mode     │  │
│  └───────────────────────────┘  │
│                                 │
│  Infrastructure Layer           │
└─────────────────────────────────┘
```

No new services, interfaces, or domain types are introduced. The change is entirely
within the CLI and configuration layers.

### Key Components

#### 1. DefaultsConfig.Mode Property

Add a `Mode` string property to `DefaultsConfig` in `TwigConfiguration.cs`, defaulting
to `"sprint"`. This is serialized via the existing `TwigJsonContext` source-gen context
(no new `[JsonSerializable]` attribute needed since `DefaultsConfig` is already registered
as part of `TwigConfiguration`).

```csharp
public sealed class DefaultsConfig
{
    // ... existing properties ...
    public string Mode { get; set; } = "sprint";
}
```

Add a `defaults.mode` case to `TwigConfiguration.SetValue()`:

```csharp
case "defaults.mode":
    var modeLower = value.ToLowerInvariant();
    if (modeLower is not ("sprint" or "workspace"))
        return false;
    Defaults.Mode = modeLower;
    return true;
```

#### 2. Init Wizard — Git Root Warning (FR-4, FR-5)

Before creating `.twig/`, check for `.git` alongside:

```csharp
var targetDir = _paths.StartDir;
var gitDir = Path.Combine(targetDir, ".git");
if (!Directory.Exists(gitDir) && !File.Exists(gitDir)) // .git can be a file in worktrees
{
    if (consoleInput.IsOutputRedirected)
    {
        // Non-TTY: skip warning, proceed
    }
    else
    {
        Console.Error.WriteLine(fmt.FormatWarning(
            $"⚠ No .git directory found at {targetDir}. " +
            "This may not be a repository root."));
        Console.Error.Write("Continue? [y/N]: ");
        var response = consoleInput.ReadLine();
        if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Aborted.");
            return (1, false, 0);
        }
    }
}
```

This check runs early in `ExecuteCoreAsync`, before `Directory.CreateDirectory(twigDir)`.

#### 3. Init Wizard — Workspace Mode Prompt (FR-1, FR-2, FR-3)

After detecting process template and before saving the final config:

```csharp
if (!consoleInput.IsOutputRedirected)
{
    Console.Write("Default workspace mode? [sprint/workspace] (sprint): ");
    var modeInput = consoleInput.ReadLine()?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(modeInput) || modeInput == "sprint")
        config.Defaults.Mode = "sprint";
    else if (modeInput == "workspace")
        config.Defaults.Mode = "workspace";
    else
        config.Defaults.Mode = "sprint"; // fallback
}
else
{
    config.Defaults.Mode = "sprint"; // non-TTY default
}
```

#### 4. IConsoleInput Injection into InitCommand

`InitCommand` currently does not accept `IConsoleInput`. We need to add it to both
constructors:

- Production constructor: add `IConsoleInput consoleInput` parameter
- Test constructor: add `IConsoleInput? consoleInput` parameter (nullable, fallback to
  a no-op implementation for backward compat with existing tests)

Update `CommandRegistrationModule.AddCoreCommands` to inject `IConsoleInput` into
the `InitCommand` factory.

#### 5. GroupedHelp Category Restructuring (FR-7, FR-8, FR-9)

Reorganize the `Show()` help text:

**Before:**
```
Views:
  status, tree, workspace, ws, sprint

Work Items:
  state, states, batch, note, update, edit, new, link *, seed *, discard, sync
```

**After:**
```
Views:
  status, tree

Workspace:
  workspace, ws, sprint

Work Items:
  state, states, batch, note, update, edit, new, link *, discard, sync

Seeds:
  seed new, seed edit, seed discard, seed view, seed link, seed unlink,
  seed links, seed chain, seed validate, seed publish, seed reconcile
```

The `KnownCommands` HashSet does NOT change — all command tokens remain.
Only the rendered help text reorganizes which category each command appears in.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Mode stored as string, not enum | AOT-safe; avoids JSON converter complexity; `"sprint"` / `"workspace"` are the only valid values, validated in `SetValue()` |
| Git warning uses `.git` directory check, not `git rev-parse` | Avoids subprocess spawn during init; `.git` directory/file presence is sufficient |
| Prompt skipped in non-TTY | Matches existing patterns (e.g., `StateCommand` backward-transition confirmation); prevents CI/script breakage |
| No `area` command in Workspace category | No standalone `area` command exists; grouping a non-existent command would break `GroupedHelpTests` completeness checks |
| Seeds category is separate from Work Items | Seed commands form a cohesive sub-workflow; separating them reduces Work Items from ~20 to ~10 entries |

## Dependencies

### External
- None (no new NuGet packages)

### Internal
- `IConsoleInput` interface (already exists in `Twig.Domain.Interfaces`)
- `TwigJsonContext` source-gen context (no changes needed — `DefaultsConfig` is
  already part of `TwigConfiguration` serialization graph)

### Sequencing
- Task T1 (config model) must complete before T2 (init wizard changes)
- T3 (help categories) is independent of T1/T2

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| Q1 | Should the mode prompt offer more than two choices (e.g., `kanban`, `custom`)? | Low | The scope specifies sprint/workspace only. Future modes can be added without breaking the config schema. |
| Q2 | Should `--force` reinit also re-prompt for mode, or preserve the existing mode? | Low | Re-prompting on `--force` is the expected behavior since the user is intentionally reinitializing. The existing config is deleted anyway. |
| Q3 | The scope mentions an `area` command in the Workspace category, but no `area` command exists. Should we create one? | Low | Out of scope for this issue. The help category will group only existing commands. If `area` is added later, it naturally slots into the Workspace category. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | No new files needed |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Infrastructure/Config/TwigConfiguration.cs` | Add `Mode` property to `DefaultsConfig`; add `defaults.mode` case to `SetValue()` |
| `src/Twig/Commands/InitCommand.cs` | Add `IConsoleInput` parameter to both constructors; add git root warning check; add workspace mode prompt; wire mode into config before save |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Update `InitCommand` factory to inject `IConsoleInput` |
| `src/Twig/Program.cs` | Reorganize `GroupedHelp.Show()` text: add Workspace and Seeds categories, remove moved commands from Views and Work Items |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Add tests for mode prompt (default, explicit sprint, explicit workspace, non-TTY skip) and git root warning (abort, continue, non-TTY skip) |
| `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` | Update `ShowUnknown_WritesErrorToStderrAndHelpToStdout` to verify new categories; existing completeness tests should pass without changes |
| `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Add test for `SetValue("defaults.mode", ...)` with valid/invalid values |

## ADO Work Item Structure

Parent Issue: **#1952 — Init & Help Updates**

---

### Issue #1966: Update init wizard: workspace mode prompt and .git location warning

**Goal:** During `twig init`, ask user for default workspace mode and warn on non-git-root
locations. Persist mode preference in workspace config.

**Prerequisites:** None

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | Add `Mode` property to `DefaultsConfig` and `defaults.mode` case to `SetValue()` | `TwigConfiguration.cs` | ~20 LoC |
| T2 | Add `IConsoleInput` to `InitCommand` constructors and DI factory; add git root warning logic before `.twig/` creation | `InitCommand.cs`, `CommandRegistrationModule.cs` | ~40 LoC |
| T3 | Add workspace mode prompt after process template detection; persist in config | `InitCommand.cs` | ~25 LoC |
| T4 | Unit tests: mode prompt (default/sprint/workspace/non-TTY), git warning (abort/continue/non-TTY), config SetValue for defaults.mode | `InitCommandTests.cs`, `TwigConfigurationTests.cs` | ~120 LoC |

**Acceptance Criteria:**
- [ ] `twig init` prompts for workspace mode when TTY is available
- [ ] Mode defaults to `sprint` when user presses Enter or in non-TTY
- [ ] `twig init` warns when no `.git` directory exists alongside `.twig`
- [ ] Warning is skipped in non-TTY environments
- [ ] User can abort init by declining the git warning
- [ ] `defaults.mode` is persisted in `.twig/config`
- [ ] `twig config defaults.mode workspace` works correctly
- [ ] All existing init tests pass

---

### Issue #1967: Add Workspace and Seeds help categories to grouped help output

**Goal:** Restructure `twig --help` to show dedicated Workspace and Seeds categories,
improving command discoverability.

**Prerequisites:** None (independent of #1966)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T5 | Reorganize `GroupedHelp.Show()` text: create Workspace category (workspace, ws, sprint), create Seeds category (all seed * commands), slim down Views and Work Items | `Program.cs` | ~40 LoC |
| T6 | Update `GroupedHelpTests` assertions: verify new category headings appear in output, verify moved commands appear in correct sections | `GroupedHelpTests.cs` | ~30 LoC |

**Acceptance Criteria:**
- [ ] `twig --help` output contains "Workspace:" section with `workspace`, `ws`, `sprint`
- [ ] `twig --help` output contains "Seeds:" section with all seed commands
- [ ] `workspace`, `ws`, `sprint` no longer appear under "Views:"
- [ ] Seed commands no longer appear under "Work Items:"
- [ ] All `GroupedHelpTests` pass (completeness, examples, known commands)
- [ ] `KnownCommands` HashSet is unchanged

## PR Groups

| PG | Name | Issues/Tasks | Type | Est. LoC | Est. Files | Predecessor |
|----|------|-------------|------|----------|------------|-------------|
| PG-1 | Init wizard enhancements | #1966 (T1, T2, T3, T4) | Deep | ~205 | 5 | — |
| PG-2 | Help category restructuring | #1967 (T5, T6) | Wide | ~70 | 2 | — |

### PG-1: Init wizard enhancements

**Scope:** Config model change (`DefaultsConfig.Mode`), `InitCommand` changes
(IConsoleInput injection, git root warning, mode prompt), DI wiring, and all
associated unit tests.

**Rationale:** These changes are tightly coupled — the config model, the init
command, and the DI wiring must ship together. Tests validate the integrated
behavior.

**Classification:** Deep — few files, but the init command logic is nuanced
(TTY detection, prompt flow, error paths).

### PG-2: Help category restructuring

**Scope:** `GroupedHelp.Show()` text reorganization and test updates.

**Rationale:** This is purely a help text change with no runtime behavior impact.
Can be reviewed independently and merged in any order relative to PG-1.

**Classification:** Wide — touches the large help string literal and test
assertions, but the changes are mechanical (move text between sections).

**Execution Order:** PG-1 and PG-2 are independent and can be executed in parallel.
No merge conflicts expected since they touch different sections of `Program.cs`
(help text vs command registration).
