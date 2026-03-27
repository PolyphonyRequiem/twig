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

## Coding Conventions

- Prefer `sealed` classes and `sealed record` types
- Use primary constructors for DI injection
- Register services in `TwigServiceRegistration.cs` (domain/infra) or `Program.cs` (commands)
- New serializable types must be added to `TwigJsonContext` with `[JsonSerializable]`
- Test projects mirror source project structure (e.g., `Services/` → `Services/`)
- Tests use MSTest v4 with `[TestClass]` / `[TestMethod]`

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
- **Always add a description** after creating and publishing a seed:
  `twig set <id>` → `twig update System.Description "..."` → `twig save`
- Descriptions should explain what, why, and acceptance criteria (2-5 sentences, plain text)

### Commit conventions
When committing code that implements plan work:
- Include `AB#<id>` in the commit message (the Issue ID from the mapping file)
- After committing, transition the ADO item: `twig set <id>` then `twig state Done`
- When all epics complete, also transition the plan-level Epic to Done
- If no mapping exists for the current work, commit normally without AB# reference
