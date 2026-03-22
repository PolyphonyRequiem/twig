# Twig View Differentiation — Scenario-Driven Clarity

> **Status:** Draft  
> **Date:** 2026-03-21  
> **Revision:** 1

---

## Executive Summary

Twig has four view commands (`status`, `tree`, `workspace`, `sprint`) plus three aliases (`show`, `ws`, `tui`). Today, `workspace`, `show`, `ws`, and `sprint` all resolve to the same `WorkspaceCommand` — with `sprint` presetting `all=true`. The overlap creates user confusion: *When do I use `tree` vs `workspace` vs `show` vs `sprint`?* This plan clarifies each view's purpose through scenario-driven analysis, identifies minimal changes to sharpen differentiation, and proposes small UX improvements that make each view obviously distinct. The philosophy is **rename/re-scope**, not **rewrite** — the rendering infrastructure is solid and needs no structural changes.

---

## Current State

| Command | Aliases | What it shows | Key flag |
|---------|---------|---------------|----------|
| `status` | — | Single active item detail + pending changes + git context + cache freshness | — |
| `tree` | — | Parent chain → focused item → children (hierarchy view) | `--depth`, `--all` |
| `workspace` | `show`, `ws` | Active context + sprint items + seeds (table view) | `--all` (show everyone's items) |
| `sprint` | — | Same as `workspace --all` (all team members) | — |

### Observations

1. **`show` and `ws` are redundant aliases** — they map identically to `workspace` with no behavioral difference. Users don't know which to use.
2. **`sprint` is just `workspace --all`** — the separate command name implies it does something different, but it's purely a flag preset.
3. **`tree` and `workspace` serve genuinely different purposes** — hierarchy navigation vs. sprint-scoped list — but their names don't communicate this clearly.
4. **`status` is well-differentiated** — it's the single-item detail view and has no overlap confusion.
5. **There's no "what am I working on right now?" one-liner** — `status` shows detail, `workspace` shows everything in the sprint. There's no quick-glance command.

---

## Scenarios

### Scenario 1: "What am I working on right now?"

**User intent:** Quick glance — see the active item ID, title, state, and whether it's dirty. Takes <1 second to read.

**Current answer:** `twig status` — but this shows a dashboard with pending changes, git context, cache freshness, and hints. More information than needed for a glance.

**Recommendation:** `twig status` already serves this well in its minimal output mode (`--output minimal`). No change needed — but the **default `twig` command with no arguments** could show this quick-glance view. Currently, `twig` with no args shows help text.

**Change:** Route `twig` (no subcommand) to `status` instead of help. Users who want help can use `twig --help`.

### Scenario 2: "What does my sprint look like?"

**User intent:** See all work items assigned to me in the current sprint, grouped or listed with state indicators. Focus is on *my* work.

**Current answer:** `twig workspace` / `twig show` / `twig ws` — shows active context + my sprint items + seeds.

**Recommendation:** Keep `workspace` as the primary command for "my sprint view." Drop `show` as an alias (it's vague). Keep `ws` as a short form.

**Change:** Remove `show` alias. `workspace` / `ws` = "my sprint items."

### Scenario 3: "What's the team working on?"

**User intent:** See all items in the current sprint across the team. Standup view.

**Current answer:** `twig sprint` (= `workspace --all`).

**Recommendation:** `sprint` is the right name and behavior. Keep it. Its purpose is clear: team-wide sprint view.

**Change:** None. `sprint` stays as `workspace --all`.

### Scenario 4: "Where does this item sit in the hierarchy?"

**User intent:** See parent chain, the focused item, and its children. Navigate up/down. Understand context.

**Current answer:** `twig tree`.

**Recommendation:** `tree` is the right name. No confusion.

**Change:** None.

### Scenario 5: "Show me the details of the active item"

**User intent:** Full detail — type, state, assigned to, area, iteration, pending changes, git branch, PRs, hints.

**Current answer:** `twig status`.

**Recommendation:** `status` is perfect for this.

**Change:** None.

### Scenario 6: "I don't have context — what do I type first?"

**User intent:** Just opened the terminal, wants to orient. May not remember if they "set" an item.

**Current answer:** `twig` shows help text. No orientation.

**Recommendation:** `twig` (no args) → quick status summary. If no active item, show workspace instead. This creates a natural "home screen."

**Change:** Default no-args behavior routes to status (or workspace if no active context).

### Scenario 7: "I want to browse/explore the backlog interactively"

**User intent:** Full-screen navigation, drill into items, keyboard-driven exploration.

**Current answer:** `twig tui`.

**Recommendation:** `tui` is correct and well-differentiated. No change.

**Change:** None.

---

## Proposed Changes

### EPIC-001: Sharpen command identity (naming & routing)

| # | Change | Impact | Risk |
|---|--------|--------|------|
| 1 | **Remove `show` alias** — `twig show` currently maps to `workspace`. The word "show" is too generic; it could mean "show active item" or "show sprint" or "show tree." Removing it forces users toward specific commands. | Low — `show` users switch to `workspace` or `ws` | Low — breaking change for `show` users, but it's an alias, not the primary command |
| 2 | **Default no-args → smart landing** — `twig` (no subcommand) currently shows help. Change to: if active context exists → run `status`; if no context → run `workspace`. Output must include a trailing hint line: `Tip: run twig --help to see all commands`. | Medium — removes the "blank screen" experience for new-ish users | Low — help discoverability preserved via hint |
| 3 | **Update help text grouping** — Rename the "Context" group to "Views" and reorder: `status` (active item), `tree` (hierarchy), `workspace`/`ws` (my sprint), `sprint` (team sprint) | Low — cosmetic | None |
| 4 | **Add short descriptions to grouped help** — Each view command gets a one-line purpose:  `status` → "Active item detail", `tree` → "Work item hierarchy", `workspace` → "My sprint items", `sprint` → "Team sprint items" | Low — cosmetic | None |
| 5 | **Auto-refresh on init** — `twig init` currently creates config + schema but does NOT sync sprint items. After init completes, automatically run the refresh flow so the user has a populated workspace immediately. Today, `twig init` → `twig workspace` shows nothing until the user manually runs `twig refresh`. Also clean up the dead code: `InitCommand` stores `config.Defaults.IterationPath` but nothing reads it — remove that write. | Medium — eliminates the "empty workspace after init" gap | Low — refresh is idempotent |

### EPIC-002: Quick-glance improvements

| # | Change | Impact | Risk |
|---|--------|--------|------|
| 1 | **`status` header line** — When output is human format, prepend a one-line summary before the detail: `#12345 ● Bug — Fix login timeout [Active]`. This gives the "glance" answer before the full dashboard. | Low — additive to existing rendering | None |
| 2 | **`workspace` active context highlight** — The active item in the sprint list should be visually distinct (bold or marker) so it's obvious which item has focus. Currently the caption shows it, but the row itself isn't highlighted. | Low — style tweak in SpectreRenderer | None |

---

### EPIC-003: Process-aware workspace display

The workspace/sprint view currently renders a flat table regardless of process template. What's *useful* to show depends on the process configuration — which is already available via `IProcessTypeStore` and `ProcessConfiguration` (fetched during `twig refresh`).

| # | Change | Scenario | Impact |
|---|--------|----------|--------|
| 1 | **Group sprint items by state category** — Instead of a flat list, group items under `Proposed`, `In Progress`, `Resolved`, `Completed` headers (using `StateCategoryResolver`). Categories with no items are omitted. This works across all process templates because `StateCategory` is the universal ADO abstraction above process-specific state names. | "What's in flight vs. what's done?" — the most common standup question | Medium — changes workspace rendering but uses existing `StateCategory` infrastructure |
| 2 | **Show process-aware type hierarchy hints** — When `--all` (team view), indent child types under parent types if the process defines a hierarchy (e.g., Tasks indented under their User Story). Uses `ProcessConfiguration.ValidChildTypes` to determine nesting. | "Which tasks belong to which story?" — standup context | Medium — requires grouping logic in workspace data preparation |
| 3 | **Progress indicators per parent** — For items that have children in the sprint, show a compact progress indicator: `[2/5]` (resolved+completed / total children). Uses cached child data from `WorkingSetService`. | "How close is this feature to done?" | Low-medium — computation from existing cached data |
| 4 | **Assignee column in team view** — When `sprint` (team view / `--all`), add an `Assigned` column. Omit it in personal `workspace` view (redundant — it's all you). | "Who's doing what?" — standup essential | Low — conditional column in table rendering |

**Process dependency:** All changes use `ProcessConfiguration` and `StateCategory` — the two process-agnostic abstractions already in the domain. No process-specific branching (no "if Agile do X, if Scrum do Y"). The state category grouping works because ADO maps every process-specific state to one of 5 categories (Proposed, InProgress, Resolved, Completed, Removed).

**Prerequisite:** EPIC-001 should land first so the command surface is stable before changing what `workspace` renders.

---

### EPIC-004: Data-driven dynamic columns

Today, the workspace/sprint table is hard-coded to 4 columns (ID, Type, Title, State) in `SpectreTheme.CreateWorkspaceTable()`. The `WorkItem.Fields` dictionary is already populated from `fields_json` (the full ADO payload stored on every cached item) and flows through `WorkspaceDataChunk` — but nobody reads it for tabular views. This EPIC makes column selection data-driven: discover which fields your team actually uses, render them automatically, and let config override when needed.

**Design principle:** No type-keyed maps, no process branching. Column selection is determined by what fields are populated in the cached data — not by what process template you're using. If your team fills in Story Points, that column appears. If nobody uses Priority, it doesn't. The data tells us what matters.

#### Architecture

```
┌──────────────────────────────────────────────────────┐
│  FieldProfileService (new, domain layer)             │
│  ─────────────────────────────────────────────────── │
│  Input:  IReadOnlyList<WorkItem> (from cache)        │
│  Output: FieldProfile per work-item-type             │
│          { RefName, FillRate, SampleValues }         │
│                                                      │
│  Algorithm:                                          │
│   1. Group items by Type                             │
│   2. For each type, scan Fields dictionaries         │
│   3. Compute fill rate per field (non-null count /   │
│      total items of that type)                       │
│   4. Exclude always-shown fields (Title, State,      │
│      Type, Id, AssignedTo, IterationPath, AreaPath)  │
│   5. Return fields sorted by fill rate descending    │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│  FieldDefinitionStore (new, infrastructure layer)    │
│  ─────────────────────────────────────────────────── │
│  Fetches: GET /{project}/_apis/wit/fields (once)     │
│  Caches:  SQLite `field_definitions` table           │
│  Provides: display name, data type, read-only flag   │
│  Synced:  alongside process_types on `twig refresh`  │
│  Fallback: derive display name from reference name   │
│            (split last segment, insert spaces)        │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│  ColumnResolver (new, domain service)                │
│  ─────────────────────────────────────────────────── │
│  Input:  FieldProfile[], FieldDefinition[],          │
│          config override (optional)                  │
│  Output: ordered list of ColumnSpec                  │
│          { RefName, DisplayName, DataType, Width }   │
│                                                      │
│  Resolution order:                                   │
│   1. If config specifies columns → use those         │
│   2. Else → take top N fields with fill rate > 40%   │
│   3. Cap at terminal width (leave room for core 4)   │
│   4. Format-aware: human → max 2-3 extra columns,    │
│      json → include all discovered fields            │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│  Table rendering (modified)                          │
│  ─────────────────────────────────────────────────── │
│  SpectreTheme.CreateWorkspaceTable() accepts         │
│  ColumnSpec[] instead of hard-coding 4 columns.      │
│  SpectreRenderer reads item.Fields[refName] for      │
│  each dynamic column when building rows.             │
│  HumanOutputFormatter adds aligned columns.          │
│  JsonOutputFormatter includes discovered fields      │
│  in the output payload.                              │
└──────────────────────────────────────────────────────┘
```

#### Field display name resolution

No hardcoded map. Resolution chain:
1. **FieldDefinitionStore** (cached from ADO `/fields` API) → authoritative display name
2. **Derivation fallback** (when field defs not yet cached) → `Microsoft.VSTS.Scheduling.StoryPoints` → split on `.`, take last segment `StoryPoints`, insert space before capitals → `"Story Points"`
3. **Config override** → `display.columns: { "Microsoft.VSTS.Scheduling.StoryPoints": "Pts" }` lets user pick their own label

#### Data type formatting

Type information comes from FieldDefinitionStore. Format rules:
- `string` → render as-is, truncate to column width
- `integer` / `double` → right-align
- `dateTime` → relative format ("3d ago") in human output, ISO 8601 in JSON
- `html` → strip tags (reuse existing `StripHtmlTags()`), truncate
- `treePath` → show last segment only (e.g., `Project\Team\Sub` → `Sub`)

#### Config surface

```yaml
# .twig/config.yaml (or twig.json)
display:
  columns:
    workspace:              # per-view column override
      - "System.Tags"
      - "Microsoft.VSTS.Scheduling.StoryPoints"
      - "Microsoft.VSTS.Common.Priority"
    sprint:                 # team view can differ from personal view
      - "System.AssignedTo"
      - "Microsoft.VSTS.Common.Priority"
      - "System.Tags"
  fillRateThreshold: 0.4   # minimum fill rate to auto-include (default 40%)
  maxExtraColumns: 3        # cap on auto-discovered columns (default 3)
```

When `display.columns.<view>` is set, auto-discovery is skipped for that view — the user's list is used verbatim. `fillRateThreshold` and `maxExtraColumns` tune auto-discovery behavior.

| # | Task | Scope | Dependencies |
|---|------|-------|--------------|
| 1 | **FieldProfileService** — Domain service that accepts a list of work items, groups by type, computes per-field fill rates, and returns ranked field profiles. Pure computation on cached data, no API calls. | `Twig.Domain/Services/` | None |
| 2 | **FieldDefinitionStore** — Infrastructure service that fetches `GET /{project}/_apis/wit/fields`, caches results in a new SQLite `field_definitions` table (ref_name, display_name, data_type, is_read_only, last_synced_at). Synced during `twig refresh` alongside process types. Fallback derivation when not yet synced. | `Twig.Infrastructure/Ado/`, `Twig.Infrastructure/Persistence/` | None (parallel with Task 1) |
| 3 | **ColumnResolver** — Domain service that combines FieldProfile + FieldDefinition + config override to produce an ordered `ColumnSpec[]`. Applies fill rate threshold, max column cap, and terminal width awareness. | `Twig.Domain/Services/` | Tasks 1, 2 |
| 4 | **Dynamic table rendering** — Modify `SpectreTheme.CreateWorkspaceTable()` to accept `ColumnSpec[]`. Modify `SpectreRenderer.RenderWorkspaceAsync()` to read `item.Fields[refName]` for each dynamic column. Apply data-type formatting. Update `HumanOutputFormatter` equivalently. | `Twig/Rendering/`, `Twig/Formatters/` | Task 3 |
| 5 | **Config surface** — Add `display.columns`, `display.fillRateThreshold`, `display.maxExtraColumns` to `TwigConfiguration.DisplayConfig`. Wire into `ColumnResolver`. | `Twig.Infrastructure/Config/` | Task 3 |
| 6 | **JSON formatter inclusion** — When fields are discovered (or configured), include them in JSON output payload alongside core properties. | `Twig/Formatters/` | Task 3 |

**API citizenship:** One new cached GET per project (`/fields`), synced on `twig refresh`. Zero ongoing cost. FieldProfileService is pure local computation on already-cached `fields_json` data.

**Prerequisite:** None — this EPIC is independent of EPICs 1-3 and can be developed in parallel. However, if EPIC-003 (state category grouping) lands first, the dynamic columns render inside the grouped layout rather than the flat table.

---

## What This Plan Does NOT Change

- **Rendering infrastructure** — `RenderingPipelineFactory`, `SpectreRenderer`, `IOutputFormatter` are untouched.
- **Data models** — `WorkTree`, `Workspace`, `WorkspaceDataChunk` are unchanged.
- **Tree command** — already well-differentiated.
- **Sprint command** — already clear in purpose.
- **TUI** — separate concern.
- **JSON/minimal output** — already stable for scripting.
- **Oh My Posh / prompt integration** — separate plan ([twig-ohmyposh.plan.md](twig-ohmyposh.plan.md)).
- **Tree enhancements (sibling counts, links)** — separate plan ([twig-tree-enhancements.plan.md](twig-tree-enhancements.plan.md)).

---

## Decision Log

| Decision | Rationale |
|----------|-----------|
| Remove `show` but keep `ws` | `ws` is an obvious abbreviation of `workspace`. `show` is ambiguous — it could mean any view. Keeping one alias (not three) reduces confusion. |
| Default no-args → status/workspace | Every CLI benefits from a "home screen." Help text is for `--help`. The default should orient the user. |
| Don't merge `tree` and `workspace` | They serve fundamentally different mental models: hierarchy navigation vs. sprint-scoped list. Merging would create a confusing hybrid. |
| Don't rename `sprint` | It's clear, it's a preset, and teams use "sprint" as a daily vocabulary word. |
| Don't add new commands | The 4-view model (status, tree, workspace, sprint) is sufficient. Adding more creates the same confusion we're trying to reduce. |
| Keep `workspace` over `sprint` as "my view" | A developer's daily work isn't always sprint-scoped. `workspace` implies broader context (seeds, active item, sprint items). `sprint` implies the team view. |
| Current iteration is always live | `GetCurrentIterationAsync()` calls ADO with `$timeframe=current` on every command that needs it — sprint rollover is automatic. InitCommand writes `config.Defaults.IterationPath` but nothing reads it (dead code, remove in EPIC-001 Task 6). |
| Auto-refresh on init | After `twig init`, the cache is empty. Users must run `twig refresh` before `workspace` shows anything. This is a poor first experience — init should leave the user with a populated workspace. |

---

## Implementation Sequence

```
EPIC-001: Naming & routing (6 tasks, ~low-medium effort)
  ├── Task 1: Remove `show` alias from Program.cs + CommandRegistrationModule
  ├── Task 2: Add default no-args → status/workspace routing with --help hint
  ├── Task 3: Update help text grouping and descriptions
  ├── Task 4: Update tests for removed alias + new default behavior
  ├── Task 5: Auto-refresh on init (call refresh flow after init completes)
  └── Task 6: Remove dead IterationPath write from InitCommand

EPIC-002: Quick-glance improvements (2 tasks, ~low effort)
  ├── Task 1: Add summary header line to StatusCommand human output
  └── Task 2: Highlight active item row in WorkspaceCommand Spectre rendering

EPIC-003: Process-aware workspace display (4 tasks, ~medium effort)
  ├── Task 1: Group sprint items by StateCategory in workspace rendering
  ├── Task 2: Process-hierarchy indentation for team view (--all)
  ├── Task 3: Progress indicators per parent item
  └── Task 4: Conditional Assignee column in team view

EPIC-004: Data-driven dynamic columns (6 tasks, ~medium-high effort)
  ├── Task 1: FieldProfileService — fill-rate analysis on cached data
  ├── Task 2: FieldDefinitionStore — /fields API + SQLite cache (parallel with Task 1)
  ├── Task 3: ColumnResolver — combine profile + definitions + config → ColumnSpec[]
  ├── Task 4: Dynamic table rendering in SpectreRenderer + HumanOutputFormatter
  ├── Task 5: Config surface (display.columns, fillRateThreshold, maxExtraColumns)
  └── Task 6: JSON formatter field inclusion

  Note: EPIC-004 is independent of EPICs 1-3 and can be developed in parallel.
        If EPIC-003 lands first, dynamic columns render inside grouped layout.
```

---

## View Identity Summary (After Changes)

| Command | Purpose | When to use |
|---------|---------|-------------|
| `twig` | Smart landing | Just opened terminal — orients you |
| `twig status` | Active item detail | "What am I working on? What's pending?" |
| `twig tree` | Hierarchy | "Where does this sit? What are its children?" |
| `twig workspace` / `ws` | My sprint items | "What's on my plate this sprint?" |
| `twig sprint` | Team sprint | "What's the team doing? Standup prep." |
| `twig tui` | Interactive browser | "Let me explore and drill into items" |

Each command answers a **different question**. No two commands answer the same question.
