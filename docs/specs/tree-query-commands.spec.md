# Tree, Query & Read Commands — Functional Specification

> **Domain:** Tree rendering, ad-hoc queries, read-only inspection, process discovery  
> **Commands:** `show` (enhanced), `workspace` (enhanced), `query`, `process`, `sync` (enhanced)  
> **Removed:** `tree` (merged into `show --tree` / `workspace --tree`), `refresh` (merged into `sync --pull-only`), `states` (renamed to `process`)  
> **Moved:** `area` → `workspace area`

## Design Principles

1. **Sync-first for machine output** — `json`, `ids`, and `minimal` formats
   sync synchronously before emitting output. A single complete write, no
   streaming, no async re-render.
2. **Two-pass rendering is human-only** — only the Spectre TTY path uses the
   render-sync-rerender pattern. Machine formats never see stale data.
3. **`--no-refresh` is the escape hatch** — skips sync for all formats,
   returns cached data immediately.
4. **`ids` format for all list-like commands** — bare numeric IDs, one per
   line, for shell piping (`twig query -o ids | xargs ...`).
5. **Process-agnostic** — no hardcoded types, states, or field names. All
   process data comes from `IProcessConfigurationProvider` at runtime.

---

## Command Consolidation

### `tree` → `show --tree` / `workspace --tree`

The standalone `tree` command is **removed**. Its functionality is absorbed:

| Old command | New equivalent | Behavior |
|-------------|----------------|----------|
| `twig tree` | `twig show --tree` | Show active item's parent chain + children as tree |
| `twig tree <id>` | `twig show <id> --tree` | Show specific item's hierarchy |
| `twig tree --all` | `twig workspace --tree` | Full backlog tree (all roots) |

A hidden alias `twig tree` is retained for backward compatibility, mapping to
`twig show --tree`.

### `refresh` → `sync --pull-only`

The standalone `refresh` command is **removed**. Sync absorbs it:

| Old command | New equivalent | Behavior |
|-------------|----------------|----------|
| `twig refresh` | `twig sync --pull-only` | Pull from ADO, skip flush |
| `twig refresh --force` | `twig sync --pull-only --force` | Pull with dirty guard bypass |
| `twig sync` | `twig sync` | Flush pending + pull (unchanged) |

### `states` → `process`

The `states` command is **renamed** to `process` and expanded to expose the
full process configuration — types, states per type, and field definitions.

### `area` → `workspace area`

All `twig area` subcommands move under `twig workspace area`. See the
workspace section for details.

---

## `twig show` — View Work Item (Enhanced)

### Purpose

Display a single work item's details, optionally as a hierarchy tree. Also
serves as the no-args "what am I working on?" view when called without an ID
(shows the active item per the context-commands spec).

### Signature

```
twig show [<id>] [--tree] [--output <format>] [--no-refresh] [--batch <ids>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `id` | int? | null | Work item ID. Omit for active item. |
| `--tree` | bool | false | Render parent chain + children as hierarchy tree |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal`, `ids` |
| `--no-refresh` | bool | false | Skip sync, use cached data only |
| `--batch` | string | null | Comma-separated IDs for batch lookup |

### Behavior — Single Item (default)

1. **Resolve target** — use `id` if provided, else active item
2. **If no active item and no ID** — exit 1, suggest `twig set <id>`
3. **Format branch:**
   - **Human (Spectre TTY):** two-pass render (cache → sync → re-render)
   - **Machine (json/minimal):** sync-first, then emit single complete output
   - **`--no-refresh`:** cache-only for all formats
4. **Fetch enrichment** — children, parent, links, field definitions, status fields
5. **If `--tree`:** render as hierarchy (parent chain upward + children downward)
6. **If no `--tree`:** render as detail card (title, state, type, assignee, fields, links)
7. Return exit code 0

### Behavior — Batch

1. Parse comma-separated IDs from `--batch` value
2. Sync-first (unless `--no-refresh`)
3. Look up each ID; silently skip missing items
4. Format all found items (array for json, one-per-line for minimal/ids)
5. Return exit code 0 (even if some IDs not found)

### Output Formats

| Format | Single Item | Batch | Tree Mode |
|--------|-------------|-------|-----------|
| `human` | Rich Spectre card with live rendering | Multiple cards | Box-drawing hierarchy |
| `json` | Full JSON object with all fields | JSON array | Tree structure with depth/parent refs |
| `minimal` | Key fields, one line | One item per line | Indented text tree |
| `ids` | Single ID | One ID per line | All IDs in tree order |

### MCP Tool: `twig_show`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | int? | no | Work item ID (omit for active item) |
| `tree` | bool | no | Return hierarchy tree |
| `batch` | string | no | Comma-separated IDs |

Returns JSON equivalent. The `ids` format is not exposed in MCP (agents use
the JSON array directly).

### MCP Tool: `twig_tree` (Hidden Alias)

Retained for backward compatibility. Maps to `twig_show` with `tree=true`.

---

## `twig workspace` — Sprint Backlog View (Enhanced)

### Purpose

Display the current sprint's work item backlog. The primary "dashboard" view.
Now also the home for area path management and tree-mode rendering.

### Signature

```
twig workspace [--tree] [--all] [--flat] [--sprint-layout] [--output <format>] [--no-live] [--no-refresh]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--tree` | bool | false | Render as full hierarchy tree (replaces `twig tree --all`) |
| `--all` | bool | false | Show all items including completed/removed |
| `--flat` | bool | false | Flat list (no hierarchy grouping) |
| `--sprint-layout` | bool | false | Internal: sprint-based layout |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal`, `ids` |
| `--no-live` | bool | false | Disable Spectre live rendering |
| `--no-refresh` | bool | false | Skip sync |

### Behavior

1. **Resolve sprint context** — current iteration from workspace config
2. **Format branch:**
   - **Human (Spectre TTY, no `--no-live`):** streaming build with live
     rendering, background sync, re-render on fresh data
   - **Machine (json/minimal/ids):** sync-first, emit complete output
   - **`--no-refresh`:** cache-only for all formats
3. **Fetch working set** — items matching sprint + area path filters
4. **Build hierarchy** — group by type, compute parent chains
5. **If `--tree`:** full hierarchy tree (all roots, box-drawing)
6. **If `--flat`:** flat sorted list
7. **Default:** grouped by type with hierarchy
8. Return exit code 0

### Output Formats

| Format | Description |
|--------|-------------|
| `human` | Spectre table with type groups, hierarchy, progress bars |
| `json` | Full JSON: `{ sprint, items[], hierarchy, summary }` |
| `minimal` | One item per line: `<id>  <type>  <state>  <title>` |
| `ids` | Bare IDs, one per line — all items in sprint |

### Telemetry

**Gap:** WorkspaceCommand is not currently instrumented. Must add:
- Event: `CommandExecuted` with `command=workspace`
- Metrics: `duration_ms`, `item_count`

### MCP Tool: `twig_workspace`

Returns JSON equivalent. Supports `tree` and `all` flags.

---

## `twig workspace area` — Area Path Management

Moved from top-level `twig area`. All subcommands unchanged in behavior.

### Subcommands

```
twig workspace area view [--output <format>]
twig workspace area add <path> [--exact] [--output <format>]
twig workspace area remove <path> [--output <format>]
twig workspace area list [--output <format>]
twig workspace area sync [--output <format>]
```

### Behavior (unchanged from current `area`)

| Subcommand | Behavior |
|------------|----------|
| `view` | Show items matching configured area paths (default if no subcommand) |
| `add` | Add area path entry. `--exact` = exact match, default = UNDER (include children) |
| `remove` | Remove area path entry (case-insensitive match) |
| `list` | List configured area path entries with semantics labels |
| `sync` | Fetch team area paths from ADO, replace local config |

### Output Formats

All subcommands support `human`, `json`, `minimal`. `view` also supports `ids`.

### Telemetry

**Gap:** Not instrumented. Must add telemetry to all subcommands.

### MCP Tool

No MCP tool for area management — workspace configuration is a CLI concern.

---

## `twig query` — Ad-hoc WIQL Search

### Purpose

Search work items with filter parameters that build WIQL queries. The primary
discovery tool for finding items outside the current sprint.

### Signature

```
twig query [<search>] [--title <pattern>] [--description <pattern>]
    [--type <type>] [--state <state>] [--assigned-to <name>]
    [--area-path <path>] [--iteration-path <path>]
    [--created-since <duration>] [--changed-since <duration>]
    [--top <n>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `search` | string? | null | Free-text search (title + description) |
| `--title` | string? | null | Title contains pattern |
| `--description` | string? | null | Description contains pattern |
| `--type` | string? | null | Work item type filter |
| `--state` | string? | null | State filter |
| `--assigned-to` | string? | null | Assigned-to filter |
| `--area-path` | string? | null | Area path filter (UNDER semantics) |
| `--iteration-path` | string? | null | Iteration path filter |
| `--created-since` | string? | null | Duration filter: `7d`, `2w`, `1m` |
| `--changed-since` | string? | null | Duration filter |
| `--top` | int | 50 | Max results |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal`, `ids` |

### Behavior

1. **No args:** show summary of recent activity (sprint items grouped by state)
2. **With filters:** build WIQL WHERE clause from parameters
3. Execute WIQL via ADO REST API (always network — no cache)
4. Format and output results
5. Return exit code 0

### Output Formats

| Format | Description |
|--------|-------------|
| `human` | Table with columns: ID, Type, State, Title, Assigned To |
| `json` | JSON array of work item objects |
| `minimal` | One item per line: `<id>  <type>  <state>  <title>` |
| `ids` | Bare IDs, one per line — for piping to other commands |

### Telemetry

Already instrumented: `had_filters`, `showed_summary`, `result_count`.

### MCP Tool

**Gap:** No MCP tool for query. Agents cannot do ad-hoc searches.

**Recommendation:** Add `twig_query` MCP tool with same filter parameters.
Returns JSON format. Useful for agents discovering related items, finding
items by state, or searching before making decisions.

---

## `twig process` — Process Configuration Discovery

### Purpose

Expose the full process configuration for the current project — work item
types, valid states per type, field definitions. Replaces the narrow `states`
command with a comprehensive process discovery tool.

### Signature

```
twig process [<type>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `type` | string? | null | Filter to specific work item type |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal` |

### Behavior

1. **No args:** list all work item types with their state counts
2. **With `<type>`:** show full details for that type:
   - States with categories (Proposed, InProgress, Completed, Removed) and colors
   - Fields with reference names and types
   - Allowed transitions (if available from process config)
3. Data comes from process cache — no network call
4. If cache empty, suggest `twig sync` to populate

### Output Formats

| Format | No Args | With Type |
|--------|---------|-----------|
| `human` | Table: type name, state count, description | Sections: States (colored), Fields, Transitions |
| `json` | `{ types: [{ name, stateCount }] }` | `{ type, states: [...], fields: [...] }` |
| `minimal` | Type names, one per line | State names, one per line |

### Telemetry

**Gap:** Not instrumented (inherited from `states`). Must add.

### MCP Tool: `twig_process`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string? | no | Filter to specific work item type |

Returns JSON format. Essential for agents needing to validate state
transitions, discover field names, or understand the project's process.

---

## `twig sync` — Synchronize Cache (Enhanced)

### Purpose

Two-phase synchronization: flush pending local changes to ADO, then pull
fresh data from ADO into the local cache. Absorbs the `refresh` command.

### Signature

```
twig sync [--pull-only] [--force] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--pull-only` | bool | false | Skip flush phase, only pull (replaces `refresh`) |
| `--force` | bool | false | Bypass dirty guard in pull phase |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal` |

### Behavior

1. **Phase 1: Flush** (skipped if `--pull-only`)
   - Push all pending local changes to ADO
   - Report failures to stderr
2. **Phase 2: Pull**
   - Build WIQL query for current sprint + area paths
   - Fetch items from ADO
   - Dirty guard: skip items with local modifications (unless `--force`)
   - Conflict detection: report items with newer remote revisions
   - Post-fetch orchestration: tracked trees, cleanup policy, ancestors, working set
   - Refresh process type data and field definitions (concurrent)
   - Update cache freshness timestamp
3. **Exit code:**
   - 0 if both phases succeed
   - 1 if either phase has failures

### Output Formats

| Format | Description |
|--------|-------------|
| `human` | Progress messages, item count, conflicts listed |
| `json` | `{ flush: { ... }, pull: { iteration, itemCount, conflicts[] } }` |
| `minimal` | Counts only |

### Hidden Alias: `refresh`

`twig refresh` maps to `twig sync --pull-only`.
`twig refresh --force` maps to `twig sync --pull-only --force`.

### Telemetry

Already instrumented (inherited from refresh): `hash_changed`, `item_count`,
`duration_ms`.

### MCP Tool: `twig_sync`

Existing tool. Behavior unchanged — flush + pull. Add `pull_only` parameter.

---

## Standard Output Format Set

All commands in this domain support these formats:

| Format | Audience | Sync behavior | Description |
|--------|----------|---------------|-------------|
| `human` | Terminal user | Two-pass render (TTY) or cache-first (piped) | Rich Spectre output with colors and box-drawing |
| `json` | Machines/agents | Sync-first, single write | Complete JSON object/array |
| `minimal` | Shell scripts | Sync-first, single write | Plain text, one item per line, tab-separated |
| `ids` | Shell piping | Sync-first, single write | Bare numeric IDs, one per line |

**`ids` is supported by:** `show` (batch/tree), `workspace`, `query`,
`workspace area view`.

**`ids` is NOT supported by:** `process`, `sync`, `show` (single item detail).

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Command error (cache miss, network failure, no results) |
| 2 | Usage error (invalid arguments) |
| 130 | Cancellation (Ctrl+C) |

---

## MCP Tool Summary

| CLI Command | MCP Tool | Status |
|-------------|----------|--------|
| `twig show` | `twig_show` | **Enhance** — add `tree` flag, absorb `twig_tree` |
| `twig workspace` | `twig_workspace` | **Enhance** — add `tree` flag |
| `twig query` | `twig_query` | **New** — add ad-hoc search for agents |
| `twig process` | `twig_process` | **New** — process discovery for agents |
| `twig sync` | `twig_sync` | **Enhance** — add `pull_only` flag |
| `twig workspace area` | — | No MCP tool (workspace config is CLI) |
| `twig tree` (alias) | `twig_tree` (alias) | Hidden, maps to `twig_show` + tree |

---

## Removal Checklist

### `tree` (standalone command → hidden alias)

- [ ] Remove `TreeCommand.cs`
- [ ] Add `--tree` flag to `ShowCommand` (single item hierarchy)
- [ ] Add `--tree` flag to `WorkspaceCommand` (full backlog tree)
- [ ] Register hidden `tree` alias in `Program.cs` → `show --tree`
- [ ] Migrate tree-specific rendering logic to shared service
- [ ] Update MCP `twig_tree` to delegate to `twig_show` + tree flag
- [ ] Remove tree-specific tests, add --tree tests to show/workspace

### `refresh` (standalone command → hidden alias)

- [ ] Remove `RefreshCommand.cs`
- [ ] Add `--pull-only` flag to `SyncCommand`
- [ ] Move refresh orchestration logic into `SyncCommand` (or shared service)
- [ ] Register hidden `refresh` alias in `Program.cs` → `sync --pull-only`
- [ ] Migrate refresh tests to sync tests
- [ ] Update documentation

### `states` (renamed → `process`)

- [ ] Rename `StatesCommand.cs` → `ProcessCommand.cs`
- [ ] Expand to show types, fields, transitions (not just states)
- [ ] Register as `twig process` in `Program.cs`
- [ ] Add `twig_process` MCP tool
- [ ] Update tests

### `area` (moved → `workspace area`)

- [ ] Move `AreaCommand.cs` logic under `WorkspaceCommand` subcommands
- [ ] Register as `twig workspace area` in `Program.cs`
- [ ] Add `ids` format to `view` subcommand
- [ ] Update tests
