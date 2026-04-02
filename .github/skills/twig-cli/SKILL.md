---
name: twig-cli
description: Use the twig CLI to manage Azure DevOps work items from the terminal. Activate when creating, updating, querying, or navigating work items, or when the user asks you to interact with their ADO backlog. Covers seed creation, publishing, state transitions, field updates, tree navigation, and work item context management.
---

# Twig CLI Skill

Use `twig` commands in the terminal to manage Azure DevOps work items. This skill defines how Copilot should interact with the twig CLI on behalf of the user.

## Core Principles

1. **Always fill descriptions** — When creating work items, write a comprehensive, well-formatted description. Use `twig update System.Description "<markdown>" --format markdown` after creation. Descriptions should be rich HTML rendered from Markdown — at least 2-3 paragraphs with headings, bullet lists, bold emphasis, and code formatting. ADO renders HTML natively; make descriptions professional and scannable.
2. **Always assign to the user** — After creating and publishing, assign with `twig update System.AssignedTo "Daniel Green"`. Every work item must have an owner.
3. **Always use `--output json`** — When Copilot runs twig commands, **always** append `--output json` for machine-readable output. This applies to every command that supports `--output`: `set`, `status`, `tree`, `workspace`, `seed new`, `seed publish`, `refresh`, `new`, etc. Parse the JSON output programmatically rather than string-matching human-formatted text. Human-formatted output is for the user's terminal only.
4. **Set context before operating** — Most commands operate on the "active" work item. Use `twig set <id>` to set it.
5. **Publish seeds promptly** — Seeds are local-only until published. Always `twig seed publish --all` after creating seeds.
6. **Sync before querying** — If data seems stale, run `twig sync` to flush pending changes and pull from ADO. (`twig refresh` and `twig save` still work as hidden aliases but are deprecated.)
7. **Use `twig note --text "..."` during implementation** — Notes are pushed immediately to ADO (push-on-write). No need to run `twig save` afterward. Notes use the `--text` flag (positional args are not supported).
8. **Decompose large issues into task chains** — If an issue is estimated to take more than 30 minutes of implementation effort, decompose it into a sequence of Tasks before coding. Use `twig seed chain` to create them as successor-linked seeds under the parent Issue, then publish and execute them one at a time. Each task should be independently committable and testable. Complete each task (commit, tests pass, `twig state Done`) before starting the next.

### Note cadence

Add a note at each of these checkpoints:
- **Research complete** — Summarize findings: what files are involved, what the gap is, key design decision(s)
- **Mid-implementation** — After finishing the first major code change, note what was done and what remains
- **Tests written** — Note test count, what's covered, any edge cases deferred
- **Done** — Final note: summary of all changes, files touched, anything noteworthy for future readers

### Note content guidelines

- Keep notes **2-4 sentences** — concise, not a wall of text
- Lead with **what** was done or decided, not process narration
- Include **specific details**: file names, method names, counts, design trade-offs
- Flag **risks or open items** when they exist

### Example notes

```bash
twig note --text "Research: gap is in StatusCommand + 3 formatters. Links data already available via SyncLinksAsync. Tree view has the pattern — status view just needs the same plumbing."
twig note --text "Core impl done: added links param to BuildStatusViewAsync and both formatter overloads. Spectre renders grid rows, Human renders separator section, JSON adds links array."
twig note --text "6 tests added across HumanOutputFormatterTests and JsonOutputFormatterTests. Covers links present, empty, and null for both formatters."
```

## Work Item Creation Workflow

```bash
# 1. Set parent context
twig set <parent-id>

# 2. Create the seed
twig seed new --title "Title here" --type Issue

# 3. Publish to ADO
twig seed publish --all

# 4. Set context to the new item, assign, and add description
twig set <new-id>
twig update System.AssignedTo "Daniel Green"
twig update System.Description "# Overview

Description of the work item with **context** and motivation.

## What

Clear statement of the problem or feature.

## Why

Motivation, user-facing impact, or technical debt being addressed.

## Acceptance Criteria

- [ ] Criterion 1
- [ ] Criterion 2" --format markdown
# ^^^ twig update pushes immediately to ADO — no twig save needed
```

### Description Guidelines

When creating work items, always use `--format markdown` for System.Description and write rich, well-formatted content:

- **Length**: At least 2-3 paragraphs unless the item is truly trivial (single config change, typo fix)
- **Structure**: Use Markdown headings (`## What`, `## Why`, `## Acceptance Criteria`)
- **Formatting**: Use bullet lists, bold, code backticks, and numbered lists for scannability
- **Content**: Include context/background, the specific change, motivation, and how to verify
- **Tone**: Professional and comprehensive — someone reading the ADO board should fully understand the work without opening the code

Example of a **good** description:
```markdown
# Overview

The `twig update` command currently sends plain text to ADO description fields.
This makes descriptions hard to read in the ADO web UI where **rich HTML** is
natively supported.

## What

Add a `--format markdown` flag to `twig update` that converts the input value
from Markdown to HTML via Markdig before patching the ADO work item.

## Why

Well-formatted descriptions with headings, lists, and code blocks make work items
significantly easier to scan on the board and in sprint reviews. This is especially
important for descriptions written by automated agents (SDLC workflow) which tend
to produce longer, structured content.

## Acceptance Criteria

- `twig update System.Description "# Title\n- item" --format markdown` sends HTML
- Unknown format values return error
- No behavior change without `--format` flag
```

## Common Commands

| Command | Purpose |
|---------|---------|
| `twig set <id-or-pattern>` | Set active work item by ID or title pattern |
| `twig status` | Show active work item details |
| `twig tree` | Display work item hierarchy |
| `twig seed new --title "T" --type Issue` | Create a local seed work item |
| `twig seed publish --all` | Publish all seeds to ADO |
| `twig update <field> "<value>"` | Update a field on the active item |
| `twig state <name>` | Transition active item state (To Do, Doing, Done) |
| `twig sync` | Flush pending changes and pull remote state |
| `twig save` | *(deprecated alias for `twig sync`)* |
| `twig refresh` | *(deprecated alias for `twig sync`)* |
| `twig flow-start <id>` | Start work: set context, transition, assign, branch |
| `twig nav` | Launch interactive tree navigator |

## Work Item Types (Basic Process)

- **Epic** — Large initiative, top-level container
- **Issue** — Feature or bug, child of Epic
- **Task** — Granular work unit, child of Issue

## State Transitions (Basic Process)

`To Do` → `Doing` → `Done`

## Field Reference Names

| Display Name | Reference Name |
|-------------|---------------|
| Description | `System.Description` |
| Assigned To | `System.AssignedTo` |
| State | `System.State` |
| Area Path | `System.AreaPath` |
| Iteration Path | `System.IterationPath` |
| Priority | `Microsoft.VSTS.Common.Priority` |
| Effort | `Microsoft.VSTS.Scheduling.Effort` |

## Important: `twig update` vs `twig save`

- **`twig update <field> "<value>"`** — pushes the change **immediately** to ADO via PATCH. No `twig save` needed afterward.
- **`twig save`** — pushes **pending local changes** (queued edits from `twig edit` or editor workflows) to ADO. Only needed for batch/editor changes, not after `twig update`.
- **`twig save` has no `--force` flag** — if it says "Nothing to save", there are no pending changes (which is correct after `twig update` since it already pushed).

## Gotchas

- **Don't `twig save` after `twig update`** — it will say "Nothing to save" because `update` already pushed. This is not an error.
- **Creating root-level items (Epics)** requires workarounds today: clear context, create seed with explicit `--type`, manually set area/iteration. See #1264 for the planned `twig new` command.
- **Seed IDs are negative** — they get remapped to positive ADO IDs on publish. Always check the publish output for the new ID.
- **`twig set` before `twig update`** — `update` operates on the active item. Forgetting to `set` first will update the wrong item.

## Tips

- `twig seed new` accepts `--title` (named) or positional arg via `twig seed "title"` (backward compat)
- After `seed publish`, the seed ID is remapped — check output for the new ADO ID
- `twig update` works on the active item; set context first with `twig set`
- Use `--output json` on any command for machine-readable output
- The database is at `.twig/{org}/{project}/twig.db` — can query directly with sqlite3
- Use `sqlite3 .twig/{org}/{project}/twig.db` for ad-hoc queries (e.g., `SELECT id, title, type FROM work_items WHERE parent_id IS NULL`)
