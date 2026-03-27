---
name: twig-cli
description: Use the twig CLI to manage Azure DevOps work items from the terminal. Activate when creating, updating, querying, or navigating work items, or when the user asks you to interact with their ADO backlog. Covers seed creation, publishing, state transitions, field updates, tree navigation, and work item context management.
---

# Twig CLI Skill

Use `twig` commands in the terminal to manage Azure DevOps work items. This skill defines how Copilot should interact with the twig CLI on behalf of the user.

## Core Principles

1. **Always fill descriptions** — When creating work items, write a meaningful description. Use `twig update System.Description "<text>"` after creation since `seed new` only accepts title and type.
2. **Set context before operating** — Most commands operate on the "active" work item. Use `twig set <id>` to set it.
3. **Publish seeds promptly** — Seeds are local-only until published. Always `twig seed publish --all` after creating seeds.
4. **Refresh before querying** — If data seems stale, run `twig refresh` to sync from ADO.

## Work Item Creation Workflow

```bash
# 1. Set parent context
twig set <parent-id>

# 2. Create the seed
twig seed new --title "Title here" --type Issue

# 3. Publish to ADO
twig seed publish --all

# 4. Set context to the new item and add description
twig set <new-id>
twig update System.Description "Detailed description of the work item..."

# 5. Save changes to ADO
twig save
```

### Description Guidelines

When creating work items, always add a description that includes:
- **What**: Clear statement of the problem or feature
- **Why**: Motivation or user-facing impact
- **Acceptance criteria**: How to verify the work is done (when applicable)

Keep descriptions concise (2-5 sentences). Use plain text, not HTML.

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
| `twig save` | Push pending local changes to ADO |
| `twig refresh` | Sync local cache from ADO |
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

## Tips

- `twig seed new` accepts `--title` (named) or positional arg via `twig seed "title"` (backward compat)
- After `seed publish`, the seed ID is remapped — check output for the new ADO ID
- `twig update` works on the active item; set context first with `twig set`
- Use `--output json` on any command for machine-readable output
- The database is at `.twig/{org}/{project}/twig.db` — can query directly with sqlite3
