# Navigation Commands — Functional Specification

> **Domain:** Work item tree traversal and history navigation  
> **Commands:** `nav`, `nav up`, `nav down`, `nav next`, `nav prev`, `nav back`, `nav fore`, `nav history`  
> **Aliases:** `up`, `down`, `next`, `prev`, `back`, `fore`, `history` (bare, hidden)

## Design Principles

1. **Context-switch only** — navigation changes the active work item pointer.
   It does NOT render a full dashboard — the user calls `twig show` afterward.
   The one exception is `nav` (bare), which is an interactive tree browser.
2. **Link-aware** — sibling navigation (`next`/`prev`) follows successor and
   predecessor links when available, falling back to display order.
3. **History is persistent** — navigation history is stored in SQLite. Back/fore
   traverse the history stack. History records are pruned when branching (same
   as browser behavior).
4. **Seed-aware** — history entries with negative (seed) IDs are resolved to
   published ADO IDs at read time via `IPublishIdMapRepository`.

---

## Architecture

Two command classes, one shared history store:

| Class | Commands | Responsibility |
|-------|----------|----------------|
| `NavigationCommands` | `nav`, `up`, `down`, `next`, `prev` | Tree traversal using parent/child/sibling relationships |
| `NavigationHistoryCommands` | `back`, `fore`, `history` | Chronological history traversal |

Both delegate to `SetCommand` for the actual context switch (which records
history). `back`/`fore` bypass `SetCommand` and write to `IContextStore`
directly to avoid recording new history entries during traversal.

---

## `twig nav` — Interactive Tree Navigator

### Purpose

Launch a Spectre-based interactive tree browser. Navigate visually through
the work item hierarchy with keyboard controls.

### Signature

```
twig nav
```

No parameters. TTY-only — exits gracefully with help text if output is
redirected or no terminal is detected.

### Behavior

1. Resolve active item from context. Exit 0 with message if none.
2. Load initial node state (item, parent chain, siblings, children, links).
3. Launch `RenderInteractiveTreeAsync` — Spectre Live region with keyboard nav.
4. On commit (user selects an item): set context, record history, update prompt.
5. On cancel (Esc): exit 0, no context change.

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| No active context | Exit 0: "No active context. Use 'twig set <id>'." |
| Active item not in cache | Exit 1: "Work item #N not found in cache." |
| Non-TTY / redirected output | Exit 0: "Interactive navigation requires a terminal." |

### MCP Tool

None — inherently interactive.

---

## `twig nav up` — Navigate to Parent

### Signature

```
twig nav up [--output <format>]
twig up [--output <format>]         # hidden alias
```

### Behavior

1. Resolve active item.
2. Build work tree (parent chain + children).
3. Call `WorkTree.MoveUp()` to get parent ID.
4. If no parent: exit 1 "Already at root."
5. Delegate to `SetCommand.ExecuteAsync(parentId)`.

---

## `twig nav down` — Navigate to Child

### Signature

```
twig nav down [<idOrPattern>] [--output <format>]
twig down [<idOrPattern>] [--output <format>]    # hidden alias
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `idOrPattern` | string? | null | Numeric ID or title substring to match |

### Behavior

1. Resolve active item. Build work tree.
2. **No argument:**
   - 0 children → exit 1 "No children to navigate to."
   - 1 child → auto-navigate (delegate to SetCommand)
   - N children → interactive disambiguation (Spectre picker) or list to stderr
3. **With argument:** pattern match against children via `WorkTree.FindByPattern()`
   - Single match → navigate
   - Multiple matches → disambiguation prompt
   - No match → exit 1 "No child matches '{pattern}'."

---

## `twig nav next` / `twig nav prev` — Sibling Navigation

### Signature

```
twig nav next [--output <format>]
twig nav prev [--output <format>]
twig next [--output <format>]     # hidden alias
twig prev [--output <format>]     # hidden alias
```

### Behavior

1. Resolve active item.
2. **Link-based resolution (preferred):**
   - `next`: follow successor link where current item is source
   - `prev`: follow predecessor link where current item is target
   - Checks seed links first (unpublished seeds), then work item links
3. **Fallback: display order** — if no link found, use `GetChildrenAsync(parentId)`
   and navigate by index ±1.
4. At boundary → exit 1 "Already at first/last sibling."
5. Item has no parent → exit 1 "Cannot navigate siblings."

---

## `twig nav back` / `twig nav fore` — History Navigation

### Signature

```
twig nav back [--output <format>]
twig nav fore [--output <format>]
twig back [--output <format>]     # hidden alias
twig fore [--output <format>]     # hidden alias
```

### Behavior

1. Call `historyStore.GoBackAsync()` / `GoForwardAsync()`.
2. If null (at boundary): exit 1 "Already at oldest/newest entry."
3. **Resolve seed ID** — negative IDs mapped to published ADO IDs.
4. **Set context directly** (bypass `SetCommand` to avoid recording history).
5. Display brief confirmation (work item one-liner or `#ID` if not cached).
6. Update prompt state.

---

## `twig nav history` — View Navigation History

### Signature

```
twig nav history [--non-interactive] [--output <format>]
twig history [--non-interactive] [--output <format>]   # hidden alias
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--non-interactive` | bool | false | Force flat list (skip Spectre picker) |
| `--output` | string | `human` | Output format: `human`, `json`, `minimal` |

### Behavior

1. Load full history with cursor position.
2. Resolve seed IDs. Batch-fetch work items for enrichment.
3. **JSON:** structured output with entries array and cursor ID.
4. **Minimal:** one work item ID per line.
5. **Human (interactive):** Spectre disambiguation picker. On selection, set
   context and record visit (prunes forward history).
6. **Human (non-interactive / `--non-interactive`):** flat list with `→` marker
   on current cursor position.

### Output Formats

| Format | Description |
|--------|-------------|
| `human` | Interactive picker (TTY) or flat list (non-interactive) with type, title, state, timestamp |
| `json` | `{ entries: [{ id, workItemId, visitedAt }], currentEntryId }` |
| `minimal` | Bare work item IDs, one per line |

---

## Navigation History Store

### Interface: `INavigationHistoryStore`

```csharp
Task RecordVisitAsync(int workItemId, CancellationToken ct = default);
Task<int?> GoBackAsync(CancellationToken ct = default);
Task<int?> GoForwardAsync(CancellationToken ct = default);
Task<(IReadOnlyList<NavigationHistoryEntry> Entries, int? CursorEntryId)> GetHistoryAsync(CancellationToken ct = default);
Task ClearAsync(CancellationToken ct = default);
```

### Semantics

- **RecordVisit:** append entry, prune any forward entries (branch behavior).
- **GoBack:** move cursor one step older. Returns null at oldest.
- **GoForward:** move cursor one step newer. Returns null at newest.
- **GetHistory:** full chronological list with current cursor position.
- Seed IDs (negative) stored as-is; resolved at read time.

### Implementation: `SqliteNavigationHistoryStore`

SQLite table in workspace database. Cursor tracked as a separate row or
column in the context table.

---

## Standard Output Format

All non-interactive nav commands support `--output`:

| Format | Behavior |
|--------|----------|
| `human` | Delegates to `SetCommand` (which renders brief confirmation) |
| `json` | JSON object from `SetCommand` |
| `minimal` | Minimal text from `SetCommand` |

`nav history` has its own format handling (see above).

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Navigation succeeded, or graceful no-op (no TTY, cancel, empty history) |
| 1 | Navigation failed (no parent, no children, boundary, cache miss) |

---

## Telemetry

**Gap:** No navigation commands have telemetry. All must be instrumented:

| Command | Event Properties |
|---------|-----------------|
| `nav` (interactive) | `command=nav`, `outcome=commit\|cancel`, `duration_ms` |
| `nav up` | `command=nav-up`, `exit_code`, `duration_ms` |
| `nav down` | `command=nav-down`, `exit_code`, `had_disambiguation`, `duration_ms` |
| `nav next` | `command=nav-next`, `exit_code`, `used_link`, `duration_ms` |
| `nav prev` | `command=nav-prev`, `exit_code`, `used_link`, `duration_ms` |
| `nav back` | `command=nav-back`, `exit_code`, `duration_ms` |
| `nav fore` | `command=nav-fore`, `exit_code`, `duration_ms` |
| `nav history` | `command=nav-history`, `exit_code`, `entry_count`, `duration_ms` |

Safe properties only — no work item IDs, titles, or types in telemetry.

---

## MCP Parity

No MCP tools for navigation. Navigation is an inherently interactive,
human-driven activity. Agents navigate by calling `twig_set` with explicit IDs.

---

## Differences from Current Implementation

**None.** The navigation commands are well-implemented and require no functional
changes. This spec documents existing behavior as the target state.

### Gaps to close (non-functional)

| Gap | Action |
|-----|--------|
| Telemetry | Add instrumentation to all 8 commands |
| Help text | Add examples and parameter documentation |
| `Console.Error.WriteLine` / `Console.WriteLine` | Migrate to injected `TextWriter` for testability |
