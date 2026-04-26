# Twig — Copilot Instructions

## Project Overview

Twig is an **AOT-compiled .NET 10 CLI** for Azure DevOps work-item triage. It renders
sprint backlogs as rich terminal trees using Spectre.Console.

**Key constraints:**
- `PublishAot=true`, `TrimMode=full`, `InvariantGlobalization=true`
- `JsonSerializerIsReflectionEnabledByDefault=false` — all JSON must use source-generated `TwigJsonContext`
- CLI framework: **ConsoleAppFramework** (source-gen, no reflection)
- Data store: **SQLite** with WAL mode, per-workspace at `.twig/{org}/{project}/twig.db`
- Rendering: **Spectre.Console** (Live regions, async rendering)
- Warnings as errors: `TreatWarningsAsErrors=true`
- Nullable reference types enabled globally
- **Process-agnostic**: No hardcoded state names, type names, or process template assumptions.
  All process-specific mapping comes from `IProcessConfigurationProvider` at runtime.
  Never assume a specific process template (Agile, Scrum, CMMI, Basic) — discover
  states, types, and fields dynamically via the provider.

## Coding Conventions

- Prefer `sealed` classes and `sealed record` types
- Use primary constructors for DI injection
- Register services in `TwigServiceRegistration.cs` (domain/infra) or `Program.cs` (commands)
- New serializable types must be added to `TwigJsonContext` with `[JsonSerializable]`
- Test projects mirror source project structure (e.g., `Services/` → `Services/`)
- Tests use **xUnit** with **Shouldly** assertions and **NSubstitute** for mocking
- Test classes use `[Fact]` and `[Theory]` attributes (not `[TestClass]`/`[TestMethod]`)

## Telemetry & Data Privacy

Twig supports optional anonymous telemetry via `TWIG_TELEMETRY_ENDPOINT` environment variable.
These rules are **non-negotiable** for ALL code that emits, collects, or transmits telemetry:

### NEVER send (even hashed)
- Organization, project, or team names
- User names, display names, or email addresses
- Process template names (e.g., "Agile", "Scrum", "CMMI")
- Work item type names (e.g., "User Story", "Bug", "Task")
- Field names or reference names (e.g., "Microsoft.VSTS.Scheduling.StoryPoints")
- Area paths or iteration paths
- Work item IDs, titles, descriptions, or any content
- Repository names, branch names, or commit hashes
- Any ADO-specific identifier or process-specific data

### Safe to send
- Twig command name (e.g., "status", "tree", "refresh")
- Command duration (ms), exit code (0/1), output format
- Twig version, OS platform
- Generic booleans (e.g., `had_profile`, `merge_needed`)
- Generic counts (e.g., `field_count: 47` — numbers only, no identifiers)

### Enforcement
- Telemetry property keys must pass an allowlist check in tests
- Keys containing "org", "project", "user", "type", "name", "path", "template",
  "field", "title", "area", "iteration", or "repo" must be rejected
- Zero network calls when `TWIG_TELEMETRY_ENDPOINT` is unset
- Telemetry failures must never affect command execution or return codes

## ADO Work Item Tracking

Twig development is tracked in `dangreen-msft/Twig` (Basic process: Epic → Issue → Task).
Plan documents map to ADO: one Epic per plan, one Issue per plan epic, optionally one Task per
task row. The mapping is stored in `tools/plan-ado-map.json`.

### Seeding conventions
- `tools/seed-from-plan.ps1` creates the ADO hierarchy from a plan document
- Plan-level Epic gets the Introduction section as its description
- Each Issue gets its epic's description text from the plan
- All items are assigned to the `-AssignedTo` param (default: "Daniel Green")
- Per-epic effort in hours can be passed via `-EpicEffortHours @{"Epic 1"=8; "Epic 2"=4}`
- After seeding, the plan Epic is transitioned to "Doing"

### Creating work items via twig CLI
- Use the `twig-cli` skill for full command reference
- **Always assign to the user** after creating and publishing a seed:
  `twig set <id>` → `twig update System.AssignedTo "Daniel Green"`
- **Always add a rich description** after creating and publishing a seed:
  `twig set <id>` → `twig update System.Description "<markdown>" --format markdown`
- Note: `twig update` pushes immediately to ADO — no `twig save` needed afterward
- **Description quality standards:**
  - Always use `--format markdown` for System.Description fields
  - Write at least 2-3 paragraphs unless the item is truly trivial
  - Use Markdown headings, bullet lists, bold, and code formatting for readability
  - Include: what the change is, why it matters, context/background, acceptance criteria
  - ADO renders the HTML natively — make it look professional

### Commit conventions
When committing code that implements plan work:
- Include `AB#<id>` in the commit message (the Issue ID from the mapping file)
- After committing, transition the ADO item: `twig set <id>` then `twig state Done`
- When all epics complete, also transition the plan-level Epic to Done
- If no mapping exists for the current work, commit normally without AB# reference

### PR Grouping Strategy

PR groups (PGs) are a cross-cutting overlay that organizes plan tasks into reviewable
pull requests. They are **not** a 1:1 mapping to the ADO work item hierarchy — grouping
is driven by review coherence.

Key heuristics:
- **2-PR sweet spot**: Two PGs per epic balances parallelism, isolation, and merge simplicity.
- **3-PR threshold**: Three PGs is where cognitive overhead increases meaningfully —
  justify reaching for 3, don't default to it.
- **Sizing guardrails**: ≤2,000 LoC and ≤50 files per PG.
- **Deep vs Wide**: Classify each PG to set reviewer expectations.

See `.github/instructions/pr-grouping.instructions.md` for the full guide.

## Work Item Lifecycle Protocol

**This protocol is mandatory for ALL agents when work is tracked in ADO work items.**
It applies whenever you create, start, or complete tasks, issues, or epics via the twig CLI.

### Starting a task

Before writing any code for a tracked task:
1. `twig set <id>` — set the active work item
2. `twig state Doing` — transition to Doing
3. `twig note --text "Starting: <brief plan of approach>"` — record intent

### During implementation

Add a `twig note` at each meaningful checkpoint — not just at completion:
- **After research/discovery** — what files are involved, what the approach is
- **After each significant code change** — what was done, what remains
- **After tests pass** — test count, coverage, edge cases deferred
- **On encountering surprises** — blockers, design changes, scope adjustments

### Completing a task

When a task's work is verified (code works, tests pass), execute in this exact order:
1. `twig note --text "Done: <summary of changes>"` — final note
2. `twig state Done` — **this is the completion event, not the todo checkbox**
3. Mark the corresponding todo as completed — this comes AFTER the state transition
4. Then proceed to the next task

**Never mark a todo completed without first running `twig state Done` on the corresponding work item.**

### After the final commit

After all tasks are complete and committed:
1. `git push`
2. Transition the parent issue: `twig set <parent-id>` → `twig state Done`
3. Then summarize — output should come last, not before operational close-out

### Why this ordering matters

The ADO state transition is the source of truth, not the todo list. If you mark a todo
"completed" without transitioning the work item, the board becomes stale and the user
has to clean up. Notes during implementation create an auditable trail. Summaries written
before `git push` and state transitions create a false sense of completion.

## MCP Server (twig-mcp)

Twig exposes an MCP server (`twig-mcp`) that provides Copilot agents with direct access to
the local work-item cache and Azure DevOps mutation operations.

### Starting the server

The server is registered in `.vscode/mcp.json` as `"twig-mcp"`. Run `./publish-local.ps1`
to build and deploy both `twig` and `twig-mcp` binaries to `~/.twig/bin/`.

### Available tools

| Tool | Description |
|------|-------------|
| `twig_set` | Set the active work item by ID or title pattern |
| `twig_status` | Show the active work item status and pending changes |
| `twig_tree` | Render the focused work item tree (parent chain + children) |
| `twig_workspace` | Show the full workspace: sprint items, seeds, dirty count |
| `twig_state` | Change the state of the active work item |
| `twig_update` | Update a field on the active work item (supports `format: "markdown"` for HTML conversion) |
| `twig_note` | Add a comment/note to the active work item |
| `twig_sync` | Flush pending local changes to ADO then refresh the local cache |

### Key behaviours

- All tools operate on the **active work item** (set via `twig_set`)
- `twig_update` with `format: "markdown"` converts Markdown to HTML (use for `System.Description`)
- `twig_note` falls back to local staging when ADO is unreachable (`isPending: true` in response)
- `twig_sync` performs a two-phase push (pending changes) then pull (active context refresh)
