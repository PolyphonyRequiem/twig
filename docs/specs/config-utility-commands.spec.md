# Config, Setup & Utility Commands Specification

> **Domain:** Workspace initialization, configuration, self-update, browser integration
> **Status:** Draft
> **Epic:** TBD

## Overview

These commands handle workspace lifecycle (init), runtime configuration (config),
self-maintenance (upgrade, changelog, version), and browser integration (web).
They have no git dependencies and operate on local state or GitHub/ADO APIs.

---

## 1. Commands

### 1.1 `init <org> <project> [--team <team>] [--git-project <name>] [--force]`

Initialize a twig workspace for an Azure DevOps organization and project.

**Behavior:**
1. Create `.twig/` directory structure (nested by org/project)
2. Initialize SQLite cache database
3. Detect ADO process template via ADO API
4. Fetch work item type appearances (icons, colors)
5. Fetch team area paths and set defaults
6. Get current iteration
7. Detect authenticated user identity
8. Sync process type state sequences
9. Sync field definitions
10. Apply or merge global profile status-fields (if profile exists)
11. Append `.twig/` to `.gitignore` (if `.git` present)
12. Prompt for default workspace mode (sprint/workspace) if TTY

**`--force`:** Deletes and recreates the current context's database. Does not
affect other org/project contexts under `.twig/`.

**`--git-project`:** Optional override for the ADO project name used in git
repository resolution (when ADO project and git project names differ).

**Exit codes:** 0 success, 1 ADO unreachable or auth failure.

**Telemetry:** `init` — `exit_code`, `duration_ms`, `had_global_profile`,
`field_count`, `output_format`.

### 1.2 `config <key> [value]`

Read or set a configuration value.

**Behavior:**
- **Read mode** (no value): Display current value for the key
- **Write mode** (value provided): Validate key, set value, save to disk
- Regenerates prompt state when `display.*` keys change

**Supported key paths:** `org`, `project`, `team`, `auth.method`, `defaults.*`,
`display.*`, `git.*`, `flow.*`, `tracking.*`, `workspace.*`, `areas.*`.

**Exit codes:** 0 success, 1 unknown key.

**Telemetry:** `config` — `exit_code`, `duration_ms` (currently missing — add).

### 1.3 `workspace fields` (moved from `config status-fields`)

Interactive field picker for configuring which fields appear in workspace
status views.

**Behavior:**
1. Fetch cached field definitions from SQLite
2. Generate status-fields config template
3. Launch external editor (`$EDITOR` or system default)
4. Parse edited config; count included fields
5. Write-back to global profile (if org + process template configured)
6. Compute field definition hash for profile versioning

**Rationale for move:** This is a workspace display concern, not a general
config operation. It configures what `workspace` and `show` commands display.

**Migration:** `config status-fields` becomes a hidden alias pointing to
`workspace fields`.

**Exit codes:** 0 success, 1 no cached field definitions (run `twig sync` first).

**Telemetry:** `workspace_fields` — `exit_code`, `duration_ms`, `field_count`.

### 1.4 `web [id]`

Open a work item in the default browser.

**Behavior:**
1. Resolve work item: explicit `id` argument or active context
2. Guard: reject seeds (negative IDs) — suggest `seed publish` first
3. Construct ADO URL: `https://dev.azure.com/{org}/{project}/_workitems/edit/{id}`
4. Launch system browser via `Process.Start()`
5. Display item title from cache

**Exit codes:** 0 success, 1 no active item or seed ID.

**Telemetry:** `web` — `exit_code`, `duration_ms` (currently missing — add).

### 1.5 `upgrade`

Check for and apply self-updates from GitHub Releases.

**Behavior:**
1. Fetch latest release from GitHub
2. Compare current version with latest (SemVer)
3. If newer: download platform-specific binary, extract, replace executable
4. Install/update companion tools (twig-mcp, twig-tui)
5. Display changelog on successful update
6. If current: check for missing companion binaries, install if needed

**Exit codes:** 0 success (or already current), 1 network/download failure.

**Telemetry:** `upgrade` — `exit_code`, `duration_ms`, `had_update`,
`from_version`, `to_version` (currently missing — add).

### 1.6 `changelog [--count <n>]`

Display recent release notes from GitHub Releases.

**Behavior:**
1. Fetch `count` releases from GitHub API (default 5, clamped 1–100)
2. Display tag, publication date, and release notes body

**Exit codes:** 0 success, 1 network failure.

**Telemetry:** `changelog` — `exit_code`, `duration_ms` (currently missing — add).

### 1.7 `version`

Display the current twig version.

**Behavior:**
1. Output version string from assembly metadata
2. Synchronous — no async I/O

**Exit codes:** 0 always.

**Telemetry:** None needed (zero-cost command).

---

## 2. Removals

### 2.1 Commands Removed (Git Integration — Epic #2153)

These commands depend on `IGitService` and are removed as part of the git
integration removal:

| Command | Reason | Notes |
|---------|--------|-------|
| `log` | Depends on `IGitService.GetLogAsync()` | Annotated git log with work item cross-ref |
| `stash` | Depends on `IGitService.StashAsync()` | Git stash with work item context |
| `stash pop` | Depends on `IGitService.StashPopAsync()` | Git stash pop with context restoration |

These join the previously identified removals: `flow start`, `flow done`,
`flow close`, `branch`, `commit`, `pr`, `link-branch` (git form), `git-context`,
`hooks install`, `hooks uninstall`, `_hook`.

**Updated removal count:** 13 commands total (was 10, now includes log, stash, stash pop).

### 2.2 Commands Relocated

| Old Location | New Location | Reason |
|--------------|--------------|--------|
| `config status-fields` | `workspace fields` | Workspace display concern, not general config |

`config status-fields` remains as a hidden alias.

---

## 3. Telemetry Gaps

| Command | Has Telemetry | Action |
|---------|---------------|--------|
| `init` | ✓ Yes | — |
| `config` | ✗ No | Add |
| `workspace fields` | ✓ Yes | — |
| `web` | ✗ No | Add |
| `upgrade` | ✗ No | Add |
| `changelog` | ✗ No | Add |
| `version` | ✗ No | Skip (zero-cost) |

---

## 4. MCP Parity

| CLI Command | MCP Tool | Notes |
|-------------|----------|-------|
| `init` | — | CLI-only (workspace lifecycle) |
| `config` | `twig_config` | Useful for agents to read/set workspace config |
| `workspace fields` | — | Requires external editor; CLI-only |
| `web` | — | Requires browser; CLI-only |
| `upgrade` | — | CLI-only (self-update) |
| `changelog` | — | Low MCP value |
| `version` | — | Low MCP value |

Only `config` is a candidate for MCP exposure.

---

## 5. Known Limitations

| Limitation | Rationale |
|------------|-----------|
| `init --force` only deletes current context DB | Multi-context safety; other org/project workspaces preserved |
| Global profile write-back failures are non-fatal | Prevents init from failing due to optional feature |
| `upgrade` has no rollback | Binary replacement is OS-level; compensating actions are fragile |
| `upgrade` has no signature verification | Acceptable for internal tooling; future improvement |
| `changelog` shows all releases (no pre-release filter) | Simplicity; filtering is a future enhancement |
| `web` uses cache-only title lookup | Avoids network call for a browser-open command |
