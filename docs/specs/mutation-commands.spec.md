# Mutation Commands — Functional Specification

> **Domain:** Work item mutation — changing state, fields, comments, and links  
> **Commands:** `state`, `update`, `note`, `edit`, `patch`, `batch`, `link parent`, `link unparent`, `link reparent`, `link artifact`  
> **Removed:** `save` (replaced by `sync`), `discard` (moved to seeds only), `link branch` (git integration removal)

## Design Principles

1. **Push-on-write** — All mutations push to ADO immediately. There is no local
   staging layer. If ADO is unreachable, the command fails loudly.
2. **No silent fallback** — Network failures are errors, not conditions to
   absorb. The user must know their change didn't land.
3. **Conflict resolution** — Commands fetch the latest revision before patching.
   On conflict, retry once automatically. On second conflict, fail with
   actionable guidance.
4. **Process-agnostic** — State names, field names, and type names are never
   hardcoded. Everything is resolved dynamically from the process configuration.

---

## `twig state` — Change Work Item State

### Purpose

Transition the active work item to a new state. Validates the transition
against the process configuration before pushing.

### Signature

```
twig state <name> [--id <int>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `name` | string | required | Target state name (full or partial, case-insensitive) |
| `--id` | int? | null | Target work item ID; omit for active item |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Behavior

1. **Validate input** — empty state name → exit 2
2. **Resolve item** — by `--id` or active context. Exit 1 if not found.
3. **Load process config** — get valid states for the item's type. Exit 1 if type not in config.
4. **Resolve state name** — partial matching, case-insensitive. Exit 1 if ambiguous or no match.
   On ambiguous match, list the matching states so the user can be more specific.
5. **Check already in state** — if already there, exit 0 with info message.
6. **Validate transition** — check if the transition is allowed by the process config. Exit 1 if not.
7. **Fetch remote** — get latest revision from ADO.
8. **Conflict resolution** — detect revision conflicts. Retry once automatically.
9. **Patch** — apply state change to ADO.
10. **Auto-push pending notes** — if any pending notes exist, push them alongside.
11. **Resync cache** — re-fetch and update local cache. Non-fatal on failure.
12. **Parent propagation** — if child moved to InProgress, try to activate parent. Best-effort.
13. **Output confirmation**

### Output

- **Human:** `✓ #42 Fix login bug → Active`
- **JSON:** `{"id": 42, "title": "...", "previousState": "To Do", "state": "Active"}`
- **Minimal:** `#42 → Active`

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | State changed (or already in target state) |
| 1 | Item not found, invalid state, disallowed transition, ADO error |
| 2 | Usage error (empty state name) |

### Telemetry

- `command`: `"state"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`

---

## `twig update` — Update a Field

### Purpose

Update a single field on the active work item. Pushes immediately to ADO.

### Signature

```
twig update <field> [<value>] [--format <convert>] [--file <path>] [--stdin]
            [--append] [--id <int>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `field` | string | required | ADO field reference name or alias |
| `value` | string? | null | Inline field value |
| `--format` | string? | null | Input conversion: `"markdown"` converts Markdown → HTML |
| `--file` | string? | null | Read value from file path |
| `--stdin` | bool | false | Read value from stdin |
| `--append` | bool | false | Append to existing value instead of replacing |
| `--id` | int? | null | Target work item ID; omit for active item |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Behavior

1. **Validate field** — empty → exit 2
2. **Validate value source** — exactly one of (inline, --file, --stdin) required. Exit 2 if 0 or 2+.
3. **Validate format** — if provided, must be `"markdown"`. Exit 2 otherwise.
4. **Resolve value** — read from source, trim, convert if `--format markdown`.
5. **Resolve item** — by `--id` or active context. Exit 1 if not found.
6. **Fetch remote** — get latest revision.
7. **Conflict resolution** — detect and retry once.
8. **Append logic** — if `--append`, get existing value and concatenate.
9. **Patch** — push field change to ADO. Exit 1 on conflict after retry.
10. **Auto-push pending notes** — push alongside if any exist.
11. **Resync cache** — re-fetch and update. Non-fatal on failure.
12. **Output confirmation**

### Value Sources

Exactly one must be provided:
- **Inline:** `twig update System.Title "New Title"`
- **File:** `twig update System.Description --file desc.md --format markdown`
- **Stdin:** `echo "content" | twig update System.Description --stdin`

### Output

- **Human:** `✓ #42 updated: System.Title = 'New Title'`
- **JSON:** `{"id": 42, "field": "System.Title", "value": "New Title", "previousValue": "Old Title"}`
- **Minimal:** `#42 System.Title updated`

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | Field updated successfully |
| 1 | Item not found, ADO error, conflict after retry |
| 2 | Usage error (empty field, no/multiple value sources, invalid format) |

### Telemetry

- `command`: `"update"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`

---

## `twig patch` — Atomic Multi-Field Update

### Purpose

Update multiple fields on a work item in a single atomic ADO PATCH request.
Designed for machine integrations (MCP agents, scripts) where multiple fields
must change together.

### Signature

```
twig patch [--json <string>] [--stdin] [--format <convert>] [--id <int>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--json` | string? | null | Inline JSON object of field→value pairs |
| `--stdin` | bool | false | Read JSON object from stdin |
| `--format` | string? | null | Input conversion: `"markdown"` converts Markdown values → HTML |
| `--id` | int? | null | Target work item ID; omit for active item |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Input Format

JSON object where keys are field reference names and values are the new values:

```json
{
  "System.Title": "New Title",
  "System.Description": "# Heading\n\nBody text",
  "Microsoft.VSTS.Common.Priority": 1
}
```

Exactly one of `--json` or `--stdin` must be provided. Exit 2 if 0 or 2.

### Behavior

1. **Validate input** — parse JSON, validate field names exist. Exit 2 on parse error.
2. **Resolve item** — by `--id` or active context. Exit 1 if not found.
3. **Format conversion** — if `--format markdown`, convert all string values
   that look like Markdown to HTML. Non-string values pass through unchanged.
4. **Fetch remote** — get latest revision.
5. **Conflict resolution** — detect and retry once.
6. **Patch** — push ALL field changes in a single ADO PATCH request (atomic).
7. **Auto-push pending notes** — push alongside if any exist.
8. **Resync cache** — re-fetch and update. Non-fatal on failure.
9. **Output confirmation**

### Output

- **Human:** `✓ #42 patched: 3 field(s) updated`
- **JSON:** `{"id": 42, "fields": {"System.Title": {"old": "...", "new": "..."}, ...}}`
- **Minimal:** `#42 patched (3 fields)`

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | All fields updated successfully |
| 1 | Item not found, ADO error, conflict after retry |
| 2 | Usage error (invalid JSON, no input, both --json and --stdin) |

### MCP Parity

`twig_patch` should be the primary MCP mutation tool for agents — atomic,
multi-field, JSON-native. `twig_update` remains for single-field convenience.

### Telemetry

- `command`: `"patch"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`
- `field_count`: number of fields in the patch

---

## `twig batch` — Multi-Item Patch

### Purpose

Apply the same mutation (state change, field updates, note) across multiple
work items in a single invocation. Internally calls the same per-item logic
as `patch`. Designed for bulk operations from scripts and agents.

### Signature

```
twig batch --ids <csv> [--state <name>] [--set <key=value>...] [--note <text>]
    [--format <convert>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--ids` | string | required | Comma-separated work item IDs |
| `--state` | string? | null | Target state for all items |
| `--set` | string[]? | null | Field updates as `key=value` pairs (repeatable) |
| `--note` | string? | null | Comment to add to each item |
| `--format` | string? | null | Input conversion: `"markdown"` converts values → HTML |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

At least one of `--state`, `--set`, or `--note` must be specified. Exit 2 otherwise.

### Behavior

1. **Parse IDs** — validate comma-separated integer list. Exit 2 on parse error.
2. **For each item:**
   a. Fetch remote (conflict check)
   b. Auto-accept-remote on conflict (no interactive prompt in multi-item mode)
   c. Validate state transition (if `--state`)
   d. Build combined field changes
   e. Single PATCH to ADO
   f. Add note (if `--note`)
   g. Resync cache (non-fatal on failure)
3. **Aggregate results** — count successes/failures
4. **Output summary**
5. Exit 0 if all succeed, exit 1 if any fail

### Relationship to `patch`

`batch` and `patch` share the same core processing logic:
- **`patch`**: single item, supports `--json`/`--stdin` structured input, interactive conflict resolution
- **`batch`**: multiple items via `--ids`, uses `--set key=value` CLI syntax, auto-accepts conflicts

`batch` should delegate per-item processing to the same `ProcessItemAsync` that
`patch` uses. The existing `BatchCommand` already implements this correctly.

### Output

- **Human:** per-item success/failure, then summary
- **JSON:** `{ totalItems, succeeded, failed, items: [{ itemId, title, success, ... }] }`
- **Minimal:** per-item one-liners, then count summary

### MCP Tool: `twig_batch`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `ids` | string | yes | Comma-separated work item IDs |
| `state` | string? | no | Target state |
| `fields` | object? | no | Field→value map (JSON object) |
| `note` | string? | no | Comment to add |
| `format` | string? | no | Input conversion (`"markdown"`) |

Returns JSON format with per-item results.

### Telemetry

- `command`: `"batch"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`
- `item_count`: number of items in batch
- `succeeded_count`: number of successful items

---

## `twig note` — Add a Comment

### Purpose

Add a comment/note to a work item. Pushes immediately to ADO.

### Signature

```
twig note [--text <string>] [--id <int>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--text` | string? | null | Inline note text; omit to open editor |
| `--id` | int? | null | Target work item ID; omit for active item |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Behavior

#### Inline mode (`--text` provided)

1. **Resolve item** — by `--id` or active context. Exit 1 if not found.
2. **Push to ADO** — call `AddCommentAsync()`. Exit 1 on failure (no staging).
3. **Resync cache** — re-fetch and update. Non-fatal on failure.
4. **Output confirmation**

#### Editor mode (no `--text`)

1. **Resolve item** — by `--id` or active context. Exit 1 if not found.
2. **Open editor** — with template showing item ID and comment instructions.
3. **Strip comments** — remove lines starting with `#`.
4. **Empty check** — if empty or unchanged, exit 0: "Note cancelled."
5. **Push to ADO** — call `AddCommentAsync()`. Exit 1 on failure (no staging).
6. **Resync cache** — re-fetch and update. Non-fatal on failure.
7. **Output confirmation**

### Output

- **Human:** `✓ Note added to #42.`
- **JSON:** `{"id": 42, "noteId": <int>, "text": "...", "createdAt": "..."}`
- **Minimal:** `#42 note added`

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | Note added (or editor cancelled) |
| 1 | Item not found, ADO unreachable, push failed |
| 2 | Usage error |

### Telemetry

- `command`: `"note"`
- `exit_code`, `output_format`, `duration_ms`, `twig_version`, `os_platform`
- `mode`: `"inline"` or `"editor"`

---

## `twig edit` — Interactive Field Editor

### Purpose

Open an external editor with all populated fields for the active work item.
On save, push changes immediately to ADO. On failure, offer retry or abort.

**This is an interactive-only command.** No JSON output, no MCP equivalent.

### Signature

```
twig edit [--field <name>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--field` | string? | null | Edit a single specific field; omit to edit all fields |

### Behavior

1. **Resolve item** — active context only (no `--id`). Exit 1 if not found.
2. **Generate editor content** — show ALL populated fields in YAML-like format,
   not just Title/State/AssignedTo. If `--field`, show only that field.
3. **Open editor** — launch with field content.
4. **Parse changes** — diff editor output against original values. Identify changed fields.
5. **No changes?** — exit 0: "No changes detected."
6. **Fetch remote** — get latest revision.
7. **Conflict resolution** — detect and retry once.
8. **Patch** — push all changed fields to ADO.
9. **On failure** — prompt: "Push failed: {error}. Retry edit, or abort? (r/A)"
   - **Retry:** re-open editor with the same content for correction
   - **Abort:** exit 1
10. **On success** — auto-push pending notes, resync cache.
11. **Output confirmation:** "Pushed N change(s) for #42."

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| Editor aborts | Exit 0: "Edit cancelled." |
| No changes | Exit 0: "No changes detected." |
| Push fails | Prompt retry/abort loop |
| Conflict after retry | Show conflict details, prompt retry/abort |

### No MCP Equivalent

`edit` is inherently interactive (editor + retry prompt). It has no MCP tool
and does not need one. Agents use `twig update` for field mutations.

### Telemetry

- `command`: `"edit"`
- `exit_code`, `duration_ms`, `twig_version`, `os_platform`

---

## `twig link parent` — Set Parent

### Purpose

Set the parent of the active work item by adding a `System.LinkTypes.Hierarchy-Reverse`
link in ADO. This is a real ADO link mutation, not local staging.

### Signature

```
twig link parent <targetId> [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `targetId` | int | required | Work item ID to set as parent |
| `--output` | string | `human` | Output format: `human`, `json`, `jsonc`, `minimal` |

### Behavior

1. Resolve active item. Exit 1 if not found.
2. **Guards:** self-parenting (exit 1), already this parent (exit 0 — no-op).
3. Already has different parent → exit 1: "Use `link reparent` to change."
4. Validate target exists in ADO. Exit 1 if not found.
5. Call `AddLinkAsync(itemId, targetId, Hierarchy-Reverse)`.
6. Resync both items (non-fatal on failure).
7. Output confirmation with updated links.

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | Link added, or already linked (no-op) |
| 1 | No active item, target not found, self-parenting, already has different parent |

---

## `twig link unparent` — Remove Parent

### Signature

```
twig link unparent [--output <format>]
```

### Behavior

1. Resolve active item.
2. If no parent → exit 1 "No parent link to remove."
3. Call `RemoveLinkAsync(itemId, parentId, Hierarchy-Reverse)`.
4. Resync both items.
5. Output confirmation.

---

## `twig link reparent` — Change Parent

### Signature

```
twig link reparent <targetId> [--output <format>]
```

### Behavior

1. Resolve active item. Run guards (self-parenting, already this parent).
2. Validate target exists.
3. Remove existing parent link (if any).
4. Add new parent link.
5. Resync all affected items (child, new parent, old parent if different).
6. Output confirmation.

---

## `twig link artifact` — Add Artifact/Hyperlink

### Purpose

Add a hyperlink (http/https URL) or artifact link (vstfs:// URI) to a work item.
Used to associate external resources — build results, wiki pages, design docs,
branch refs — with work items.

### Signature

```
twig link artifact <url> [--name <display>] [--id <int>] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `url` | string | required | HTTP/HTTPS URL or vstfs:// URI |
| `--name` | string? | null | Display name for the link |
| `--id` | int? | null | Target work item ID; omit for active item |
| `--output` | string | `human` | Output format |

### Behavior

1. Resolve item (by `--id` or active context).
2. Call `AddArtifactLinkAsync(itemId, url, name)`.
3. If already linked: exit 0 "Already linked."
4. On failure: exit 1 with error.
5. Output confirmation.

### MCP Tool: `twig_link_artifact`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | yes | URL or vstfs:// URI |
| `name` | string? | no | Display name |
| `id` | int? | no | Work item ID (omit for active) |

---

## `twig link batch` — Batch Link Operations (NEW)

### Purpose

Apply link operations across multiple work items in a single invocation.
Designed for agents and scripts that need to restructure hierarchy or
attach artifacts to many items at once.

### Signature

```
twig link batch --json <string> [--stdin] [--output <format>]
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `--json` | string? | null | Inline JSON array of link operations |
| `--stdin` | bool | false | Read JSON from stdin |
| `--output` | string | `human` | Output format |

### Input Format

JSON array of operations. Each operation specifies a type and parameters:

```json
[
  { "op": "parent", "itemId": 1234, "targetId": 5678 },
  { "op": "unparent", "itemId": 1234 },
  { "op": "reparent", "itemId": 1234, "targetId": 9012 },
  { "op": "artifact", "itemId": 1234, "url": "https://example.com", "name": "Design Doc" }
]
```

### Behavior

1. Parse and validate JSON input. Exit 2 on parse error.
2. For each operation:
   a. Resolve item by `itemId`.
   b. Execute the link operation (same logic as individual commands).
   c. Record success/failure.
3. Resync all affected items (deduplicated).
4. Output aggregate results.

### Output

- **Human:** per-operation success/failure, then summary
- **JSON:** `{ totalOps, succeeded, failed, operations: [{ op, itemId, success, error? }] }`
- **Minimal:** per-operation one-liners

### Exit Codes

| Code | Condition |
|------|-----------|
| 0 | All operations succeeded |
| 1 | One or more operations failed |
| 2 | Invalid input JSON |

### MCP Tool: `twig_link_batch`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `operations` | array | yes | Array of link operation objects |

Primary MCP tool for agents doing hierarchy restructuring.

### Telemetry

- `command`: `"link-batch"`
- `exit_code`, `output_format`, `duration_ms`
- `operation_count`, `succeeded_count`

---

## `twig link branch` — REMOVED

**Rationale:** Git integration command. Wraps branch-to-work-item artifact
linking that users can do directly via ADO UI or `link artifact` with the
vstfs:// branch URI. Removed as part of the git integration cleanup.

Remove `LinkBranchCommand.cs`, its tests, and its `Program.cs` registration.

---

## Link Commands — Telemetry

**Gap:** No link commands have telemetry. All must be instrumented:

| Command | Properties |
|---------|-----------|
| `link parent` | `command=link-parent`, `exit_code`, `duration_ms` |
| `link unparent` | `command=link-unparent`, `exit_code`, `duration_ms` |
| `link reparent` | `command=link-reparent`, `exit_code`, `duration_ms` |
| `link artifact` | `command=link-artifact`, `exit_code`, `duration_ms` |
| `link batch` | `command=link-batch`, `exit_code`, `operation_count`, `duration_ms` |

---

## `twig save` — REMOVED

**Rationale:** With push-on-write and no local staging, there's nothing to
explicitly "save." All mutations push immediately. `twig sync` handles
cache refresh and any pending operations.

Remove `SaveCommand.cs`, its tests, and its registration in `Program.cs`.

---

## `twig discard` — Seeds Only

**Rationale:** With no local staging for regular work items, there's nothing
to discard. `discard` is retained solely for seed work items.

### Signature Change

```
twig discard <id>     →  twig seed discard <id>
twig discard --all    →  twig seed discard --all
```

The top-level `discard` command is removed. Seed discard functionality
lives under `twig seed discard` (which already exists).

Remove `DiscardCommand.cs`, its tests, and its registration in `Program.cs`.

---

## Differences from Current Implementation (v0.57.0)

### Behavioral changes

| Area | Current | Target |
|------|---------|--------|
| `note` offline | Stages locally, succeeds | Fails loudly, exit 1 |
| `edit` offline | Stages locally, succeeds | Fails loudly, offers retry/abort |
| `edit` fields | Title, State, AssignedTo only | All populated fields |
| `edit` failure | Silent fallback to staging | Retry/abort prompt |
| `save` | Exists (deprecated) | Removed entirely |
| `discard` | Works on regular items + seeds | Seeds only (via `seed discard`) |
| `state`/`update` offline | Propagates error (correct) | No change needed |

### Commands removed

| Command | Replacement |
|---------|-------------|
| `save` | `sync` |
| `discard` (top-level) | `seed discard` |
| `link branch` | `link artifact` with vstfs:// URI, or ADO UI |

### MCP changes

| Tool | Action |
|------|--------|
| `twig_patch` | Add — primary agent mutation tool (multi-field, atomic) |
| `twig_batch` | Add — multi-item mutation tool |
| `twig_link_artifact` | Add — artifact/hyperlink attachment |
| `twig_link_batch` | Add — batch link operations for hierarchy restructuring |
| `twig_edit` | Remove if it exists (interactive only) |
| `twig_discard` | Remove (seeds handled by seed-specific tools) |
