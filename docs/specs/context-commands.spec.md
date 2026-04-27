# Context Commands — Functional Specification

> **Domain:** Context management — selecting, viewing, and inspecting work items  
> **Commands:** `set`, `show`  
> **Removed:** `status` (absorbed into `show`)

## Design Principles

1. **Separation of concerns** — `set` changes context, `show` displays it.
   `set` does not render dashboards. `show` does not change context.
2. **Sync-first for machine output** — JSON/jsonc/minimal formats sync
   synchronously before emitting a single complete output. No streaming,
   no async re-render.
3. **Cache-only is opt-in** — `--no-refresh` skips sync for both human and
   machine formats. Without it, read commands always produce fresh data.

---

## `twig set` — Change Active Context

### Purpose

Switch the active work item pointer. This is a **mutation-only** command — it
changes which item is "current" but does NOT render a full display. After
switching, the user calls `twig show` to see details.

### Signature

```
twig set <idOrPattern> [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `idOrPattern` | string | required | Numeric ID or title pattern (wildcard substring search) |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Behavior

1. **Validate input** — empty or whitespace → exit 2 (usage error)

2. **Resolve item**
   - **Numeric ID:** Look up in cache first. On cache miss, fetch from ADO.
     If ADO fetch fails, exit 1 with error.
   - **Pattern (non-numeric):** Search cache only (never hits ADO).
     - 0 matches → exit 1: `No cached items match '<pattern>'.`
     - 1 match → use it
     - 2+ matches (TTY) → interactive disambiguation prompt
     - 2+ matches (non-TTY or machine format) → list matches on stderr, exit 1

3. **Update context**
   - Set active work item ID in context store
   - Record visit in navigation history (enables `back`/`forward`)
   - Write prompt state (for shell prompt integration)

4. **Output confirmation**
   - **Human:** `Set active item: #42 Fix login bug [Active]`
   - **JSON:** `{"id": 42, "title": "Fix login bug", "state": "Active", "type": "Task"}`
   - **Minimal:** `#42`

5. **No sync, no rendering, no enrichment** — `set` does not load children,
   parents, links, field definitions, or compute child progress. It does not
   render a dashboard. It does not run a working set sync.

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | Context switched successfully |
| 1 | Item not found (ADO unreachable, pattern has no matches) |
| 2 | Usage error (empty input) |

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| Negative ID (seed) | Passed to resolver; resolver handles seed lookup |
| Non-integer string | Falls back to pattern search |
| Pattern with special chars | Treated as literal substring match |
| Network timeout on fetch | Exit 1 with timeout message |
| Item already active | Still records history, re-outputs confirmation |

### Telemetry

- `command`: `"set"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`
- `resolution_method`: `"id_cached"`, `"id_fetched"`, `"pattern"`
- `match_count`: number of pattern matches (for disambiguation tracking)

---

## `twig show` — Display Work Item

### Purpose

Read-only display of a work item with full enrichment. Replaces both the old
`show` (cache-only read) and `status` (active item dashboard) commands.

### Signature

```
twig show [<id>] [--output <format>] [--no-refresh] [--batch <ids>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `id` | int? | null | Work item ID. Omit to show the active item. |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |
| `--no-refresh` | bool | false | Skip sync pass — show only cached data |
| `--batch` | string | null | Comma-separated IDs for batch mode |

### Behavior — Single Item

#### No ID provided (replaces `status`)

1. **Resolve active item** — read from context store
   - No active item → branch detection hint + exit 1
   - Active item in cache → use it
   - Active item NOT in cache → auto-fetch from ADO (G-3 contract)
   - Fetch fails → exit 1: `"Work item #X not found. Run 'twig set X' to refresh."`

2. **Sync (unless `--no-refresh`)**
   - **Machine formats (json, jsonc, minimal):** sync synchronously, then output
   - **Human format (TTY):** render cached data immediately → sync in background →
     re-render with fresh data when sync completes
   - **Human format (non-TTY):** sync synchronously, then output

3. **Enrichment** (best-effort, all from cache after sync)
   - Load children → compute child progress
   - Load parent chain
   - Load links
   - Load field definitions + status-fields config
   - Load pending changes (field count + note count)
   - Load git context (current branch + linked PRs)

4. **Output**
   - See Output Formats below

#### With ID provided

Same as above, except:
- Uses the provided ID instead of the active item
- Does NOT change the active context (read-only)
- Does NOT record navigation history
- Branch detection hints are not applicable

### Behavior — Batch Mode

```
twig show --batch 10,20,30 --output json
```

1. Parse comma-separated IDs, trim whitespace, parse as integers
2. For each valid ID: look up in cache
3. **Sync (unless `--no-refresh`):** sync all found items synchronously
4. Missing IDs silently skipped (not errors)
5. Invalid numbers silently skipped
6. Output array of found items

### Output Formats

#### JSON (single item)

```json
{
  "id": 42,
  "title": "Fix login bug",
  "state": "Active",
  "type": "Task",
  "assignedTo": "user@domain.com",
  "areaPath": "Project\\Area",
  "iterationPath": "Project\\Sprint 1",
  "tags": ["frontend", "priority-1"],
  "fields": {
    "Microsoft.VSTS.Scheduling.StoryPoints": 5,
    "Microsoft.VSTS.Common.Priority": 2
  },
  "parent": {
    "id": 100,
    "title": "Parent Story",
    "state": "Active",
    "type": "User Story"
  },
  "children": [
    { "id": 43, "title": "Subtask 1", "state": "Done", "type": "Task" }
  ],
  "childProgress": { "done": 2, "total": 3 },
  "links": [
    { "rel": "Duplicate", "target": { "id": 44, "title": "Dupe", "state": "Active" } }
  ],
  "pendingChanges": {
    "fields": 1,
    "notes": 2
  },
  "dirty": false,
  "git": {
    "branch": "sdlc/42",
    "pullRequests": [
      { "id": 123, "title": "Fix login", "status": "Active" }
    ]
  }
}
```

#### JSON (batch)

```json
[
  { "id": 10, "title": "First", ... },
  { "id": 20, "title": "Second", ... }
]
```

#### Human (TTY)

Rich Spectre dashboard with panels:
- **Item panel:** ID, title, state, type, assigned, area, iteration, tags
- **Fields panel:** status-fields with values
- **Children panel:** progress bar + list
- **Parent panel:** parent chain
- **Links panel:** related items
- **Pending changes panel:** field + note counts
- **Git context panel:** branch + PR status
- **Hints panel:** stale data warnings, actionable suggestions

#### Human (non-TTY / redirected)

```
#42: Fix login bug [Active]
     Type: Task
     Area: Project\Area
     Iteration: Project\Sprint 1
     Assigned: user@domain.com

     Children: 2/3 done
     Parent: #100 Parent Story

     Pending: 1 field change, 2 notes
     Branch: sdlc/42 (PR #123: Active)
```

#### Minimal

```
#42: Fix login bug (Active) [2 pending]
```

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | Success |
| 1 | Item not found, active context not set, ADO unreachable |
| 2 | Usage error (invalid batch format) |

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| No active item (no args) | Branch detection hint → exit 1 |
| `--no-refresh` with stale cache | Shows cached data, hints "data may be stale" |
| Sync fails (network) | Falls back to cached data (human); exits 1 (machine) |
| Parent not in cache | Skipped in output (nullable) |
| Link load fails | Continues with empty links (best-effort) |
| Empty batch string | Returns `[]` (JSON) or empty output |
| Batch with all missing IDs | Returns `[]` (JSON) or empty output |
| Git not available | Git context omitted silently |
| PR lookup fails | PR section omitted (best-effort) |

### Telemetry

- `command`: `"show"` or `"show-batch"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`
- `mode`: `"active"`, `"by_id"`, `"batch"`
- `had_sync`: boolean (whether sync was performed)
- `item_count`: number of items in batch output

---

## `twig status` — REMOVED

**Rationale:** `status` and `show` had overlapping responsibilities with subtle
behavioral differences (sync scope, enrichment level, pending changes display).
Users couldn't reliably predict which to use. Merging into `show` provides a
single, predictable command surface:

- `twig show` (no args) = what `status` did: active item + pending changes + git
- `twig show <id>` = what `show` did: specific item display
- `twig show --no-refresh` = cache-only, no sync

### Migration Path

| Old Command | New Equivalent |
|-------------|----------------|
| `twig status` | `twig show` |
| `twig status --output json` | `twig show --output json` |
| `twig status --no-refresh` | `twig show --no-refresh` |
| `twig status --no-live` | `twig show --no-refresh` |
| `twig show <id>` | `twig show <id>` (unchanged) |
| `twig show <id> --no-refresh` | `twig show <id> --no-refresh` (unchanged) |

### Deprecation Strategy

1. Keep `status` as a hidden alias that calls `show` internally
2. Emit deprecation warning on stderr: `"warning: 'twig status' is deprecated. Use 'twig show' instead."`
3. Remove alias after 2 minor versions

---

## Differences from Current Implementation (v0.57.0)

### `set` changes needed

| Area | Current | Target |
|------|---------|--------|
| Dashboard rendering | Full Spectre dashboard with children, progress, hints | Minimal confirmation line |
| Child/parent loading | Loads children, parent chain, links, field defs | None — just resolve + set pointer |
| Working set computation | On cache miss: computes working set, evicts stale items | None — eviction moves to `show` or `sync` |
| Targeted sync | Syncs item + parent chain after context change | None — sync deferred to `show` |
| Hint engine | Generates and displays context-aware hints | None — hints move to `show` |

### `show` changes needed

| Area | Current | Target |
|------|---------|--------|
| No-args mode | Not supported (requires numeric ID) | Resolves active item from context store |
| Pending changes | Not shown | Show pending field + note counts |
| Git context | Not shown | Show branch + linked PRs |
| Branch detection hints | Not available | Show when no active item set |
| Sync for machine formats | No sync (cache-only) | Sync first, then output |
| Working set eviction | Not performed | On cache miss fetch, compute + evict |

### `status` removal

| Step | Action |
|------|--------|
| 1 | Move pending changes, git context, branch hints to `show` |
| 2 | Add hidden `status` alias pointing to `show.ExecuteAsync()` |
| 3 | Emit deprecation warning |
| 4 | Update MCP tool `twig_status` → `twig_show` |
| 5 | Update all tests |
| 6 | Remove `StatusCommand.cs` after deprecation period |
