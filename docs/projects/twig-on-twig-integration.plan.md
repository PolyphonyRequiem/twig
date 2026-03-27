# Twig-on-Twig — ADO-Backed Work Tracking for Twig Development

> **Revision**: 9
> **Date**: 2026-03-27
> **Status**: In Progress
> **Revision Notes**: R9: Mark EPIC-002 Conductor Committer Integration as DONE. Created `tools/Committer-AdoIntegration.Tests.ps1` with 18 Pester tests validating AB# linking, twig state transition commands, advisory error handling, plan-ado-map.json structure, and no-mapping-file fallback behaviour in the implement.yaml committer prompt. E2-T5 and E2-T6 completed as automated prompt-content regression tests. All acceptance criteria verified. R8: Mark EPIC-001 Bootstrap & Context Switching as DONE.Three review issues resolved in `Switch-TwigContext.ps1`: (1) `$ErrorActionPreference` moved out of script scope into function bodies to prevent caller scope pollution when dot-sourced, (2) null/empty team comparison normalized using `[string]` cast, (3) test sleep increased from 50ms to 150ms for CI robustness. All 21 tests pass. R7: Complete rewrite. Removed all `run-epics.ps1` and `epic-queue.txt` references (deleted from repo). Removed all `microsoft/OS` and CloudVault references (not twig's concern). Removed context-switching machinery (twig2 repo stays permanently on `dangreen-msft/Twig`). Redesigned around: (1) ADO hierarchy matching Basic process template (Epic → Issue → Task), (2) plan-to-ADO seeding script, (3) conductor committer AB# linking, (4) copilot instruction updates so all agents know how to update work items, (5) git commit-msg hook for AB# enforcement. Grounded against fresh `twig init --org dangreen-msft --project Twig --force` (2026-03-27).

---

## Executive Summary

This plan makes twig development self-tracking — using `dangreen-msft/Twig` in Azure DevOps to mirror all planned work. The ADO project uses the **Basic** process template (Epic → Issue → Task, states: To Do → Doing → Done). Plan documents remain the source of truth for task definitions; ADO provides board visibility, state tracking, and commit linking via `AB#` references.

The integration has four parts:

1. **ADO work item hierarchy** — Create Epics (one per plan document), Issues (one per plan epic), and optionally Tasks (one per task row) in the `dangreen-msft/Twig` project using `twig seed`.
2. **Plan-to-ADO seeding script** — A PowerShell script that parses plan documents, creates ADO work items via `twig seed new` + `twig seed publish`, and persists the ID mapping.
3. **Conductor workflow updates** — Teach the committer agent to include `AB#<id>` in commit messages and update ADO work item state on epic completion.
4. **Git guardrails** — A `commit-msg` hook that warns when commits lack `AB#` references to planned work.

The twig2 workspace is permanently configured for `dangreen-msft/Twig`. No context-switching is needed.

---

## Background

### Workspace State

The twig2 repository workspace was freshly initialized on 2026-03-27:

```
twig init --org dangreen-msft --project Twig --force
```

- **Process template**: Basic
- **Type hierarchy**: Epic → Issue → Task
- **States** (all types): To Do → Doing → Done
- **Current iteration**: `Twig\Sprint 1` (empty)
- **Database**: `.twig/dangreen-msft/Twig/twig.db` (schema v9)
- **Config**: `.twig/config` pointing to `dangreen-msft/Twig`

### Work Item Types Available

| Type | Default Child | States | Color |
|------|--------------|--------|-------|
| Epic | Issue | To Do → Doing → Done | `#E06C00` |
| Issue | Task | To Do → Doing → Done | `#339947` |
| Task | *(none)* | To Do → Doing → Done | `#A4880A` |

### ADO Hierarchy Mapping

Plan documents map naturally to the Basic hierarchy:

| ADO Type | Maps To | Granularity |
|----------|---------|-------------|
| **Epic** | Plan document (e.g., "Interactive Navigation") | One per `.plan.md` file |
| **Issue** | Epic within a plan (e.g., "Epic 1: Core Interactive Loop") | One per `### Epic N:` heading |
| **Task** | Task row within an epic (e.g., "E1-T1: Implement TreeNavigatorState") | One per task table row (optional) |

### Conductor Workflow

Implementation is driven by conductor (`implement.yaml` at `.github/skills/octane-workflow-implement/assets/implement.yaml`). The workflow runs whole plans:

```
epic_selector → coder → epic_reviewer → committer → (loop per epic) → plan_reviewer → fixer
```

The **committer agent** (lines 360–470) handles git commits and plan updates. It currently:
- Updates plan epic status to DONE
- Updates task statuses to DONE
- Checks acceptance criteria checkboxes
- Creates git commits with epic ID prefix (e.g., `"EPIC-001: Implement user authentication flow"`)

It does **not** currently: reference ADO work items, include `AB#` links, or transition ADO state.

### Incomplete Work

Two plan documents have outstanding tasks to seed as ADO work items:

| Plan | Incomplete Tasks | Epic Heading Pattern |
|------|-----------------|---------------------|
| `twig-interactive-nav.plan.md` | 15 (Not Started) | `- EPIC-NNN: Title` (bullet list, not headings) |
| `twig-on-twig-integration.plan.md` | This plan (being rewritten) | `### Epic N: Title` |

All other plans (12 files, 321+ tasks) are fully DONE.

### Twig CLI Commands Used

| Command | Purpose | Output |
|---------|---------|--------|
| `twig seed new "title"` | Create local draft work item | `{"message": "Created local seed: #-1 Title (Type)"}` |
| `twig seed publish --all --output json` | Publish drafts to ADO | `{"results": [{oldId, newId, title, status, isSuccess, ...}], "createdCount": N, ...}` |
| `twig set <id> --output json` | Set active work item | Single JSON object: `{id, title, type, state, ...}` |
| `twig state <name> --output json` | Transition state | `{"message": "#42 → Done"}` |
| `twig ws --output json` | Workspace overview | JSON with sprint items and seeds |
| `twig refresh` | Re-sync from ADO | Item counts |

---

## Problem Statement

1. **No ADO backing for twig work** — All development is tracked in plan markdown files with no ADO representation. No board visibility, no queries, no dashboards.

2. **Conductor commits are orphaned** — The committer agent creates git commits that are not linked to any ADO work items. There is no `AB#` reference, so ADO has no awareness of code changes.

3. **No state tracking** — When conductor completes an epic and marks it DONE in the plan document, no corresponding ADO state transition occurs. The ADO item stays in "To Do" forever.

4. **No agent awareness** — Conductor agents don't know which ADO work item they're implementing. They can't update descriptions, add comments, or reference the item in commits.

---

## Goals

| ID | Goal |
|----|------|
| G-1 | Create a full ADO work item hierarchy for all incomplete planned work (Epics → Issues → Tasks) |
| G-2 | Provide a reusable seeding script that creates ADO items from plan documents and records the mapping |
| G-3 | Teach the conductor committer agent to include `AB#<id>` in commit messages |
| G-4 | Transition ADO work item state to "Done" when conductor completes an epic |
| G-5 | Add a git `commit-msg` hook that warns on commits without `AB#` references |
| G-6 | Update copilot instructions so all agents (not just conductor) know about ADO tracking |
| G-7 | Require zero new twig CLI commands — compose existing `seed`, `set`, `state` commands |

## Non-Goals

- **NG-1**: MCP server for twig — valuable but separate effort
- **NG-2**: Context-switching tooling — the twig2 repo stays on `dangreen-msft/Twig`
- **NG-3**: Bi-directional sync between plan docs and ADO — plan docs remain source of truth
- **NG-4**: Modifying conductor's YAML schema — changes are limited to input variables and prompt text

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-01 | A script to parse plan EPIC headings and create ADO work items via `twig seed new` + `twig seed publish` |
| FR-02 | Support both EPIC heading patterns: `### Epic N: Title` and `### EPIC-NNN: Title` |
| FR-03 | A persistent mapping (JSON) from plan file + epic ID → ADO work item ID |
| FR-04 | The conductor committer agent includes `AB#<id>` in every commit message when an ADO mapping exists |
| FR-05 | The conductor committer agent transitions the mapped ADO Issue to "Done" after committing |
| FR-06 | A git `commit-msg` hook that warns (not blocks) when a commit lacks `AB#` references |
| FR-07 | Copilot instructions updated to document the ADO tracking convention |
| FR-08 | Seed ADO Epics for each plan document and Issues for each plan epic across all incomplete plans |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-01 | All twig commands in automation use `--output json` |
| NFR-02 | Seeding script is idempotent — re-running skips already-mapped epics |
| NFR-03 | Conductor workflow works identically when ADO mapping is absent (backward compat) |
| NFR-04 | Git hook is advisory (exit 0) — never blocks commits |

---

## Design

### Architecture

```
┌────────────────────────────┐
│  Plan Documents (.plan.md) │  Source of truth for task definitions
│  ├── Epic headings         │
│  └── Task tables           │
└──────────┬─────────────────┘
           │ seed-from-plan.ps1
           ▼
┌────────────────────────────┐
│  ADO: dangreen-msft/Twig   │  Visibility, state tracking, commit linking
│  ├── Epic (per plan doc)   │
│  │   └── Issue (per epic)  │
│  │       └── Task (per row)│  (optional)
│  └── States: To Do → Done  │
└──────────┬─────────────────┘
           │ plan-ado-map.json
           ▼
┌────────────────────────────┐
│  Conductor implement.yaml  │  Reads mapping, includes AB# in commits,
│  ├── committer agent       │  transitions Issue → Done on completion
│  └── all agents            │
└──────────┬─────────────────┘
           │ git commit
           ▼
┌────────────────────────────┐
│  Git                       │  commit-msg hook warns on missing AB#
│  └── AB#<id> in messages   │
└────────────────────────────┘
```

### Component 1: Seeding Script (`tools/seed-from-plan.ps1`)

Parses plan documents for EPIC headings, creates ADO work items at the correct hierarchy level, and records the mapping.

**Parameters**:
```powershell
param(
    [Parameter(Mandatory)][string]$PlanFile,
    [string]$PlanTitle,           # ADO Epic title (defaults to plan H1 heading)
    [string]$MapFile = "tools/plan-ado-map.json",
    [switch]$IncludeTasks,        # Also create Task items for each task row
    [switch]$DryRun
)
```

**Hierarchy creation**:
1. **Epic** (per plan document): `twig seed new --type Epic "$PlanTitle"` → publish → get ADO ID
2. **Issue** (per plan epic): `twig set $epicId` → `twig seed new "$EpicHeading"` → publish → get ADO ID
3. **Task** (per task row, if `--IncludeTasks`): `twig set $issueId` → `twig seed new "$TaskDesc"` → publish → get ADO ID

**EPIC heading parser** — supports both conventions:
```powershell
# Pattern 1: ### Epic N: Title (most plans)
# Pattern 2: ### EPIC-NNN: Title (twig-on-twig)
$epicPattern = '###\s+(?:Epic\s+(\d+)|EPIC-(\d+)):\s*(.+)'
```

**Idempotency**: Before creating any item, check `plan-ado-map.json` for an existing mapping. Skip if already seeded.

### Component 2: Plan-ADO Mapping (`tools/plan-ado-map.json`)

A JSON file (not YAML — avoids the `powershell-yaml` module dependency) recording the mapping.

```json
{
  "docs/projects/twig-interactive-nav.plan.md": {
    "epicId": 100,
    "epics": {
      "EPIC-001": { "issueId": 101 },
      "EPIC-002": { "issueId": 102 },
      "EPIC-003": { "issueId": 103 }
    }
  }
}
```

**Why JSON instead of YAML**: PowerShell has native `ConvertFrom-Json` / `ConvertTo-Json` — no third-party module needed.

### Component 3: Conductor Committer Updates

The **committer agent** prompt in `implement.yaml` (lines 396–439) is updated to:

1. **Read the mapping file**: After committing code, read `tools/plan-ado-map.json` to find the ADO Issue ID for the current plan + epic.

2. **Include AB# in commit messages**: When an ADO mapping exists, append `AB#<issueId>` to the commit message.
   - Example: `Epic 1: Implement core interactive loop AB#101`

3. **Transition ADO state**: After committing, run `twig state Done --output json` to transition the Issue to Done.

**Prompt additions** (conditional on mapping file existence):
```
After creating the git commit:

1. Check if `tools/plan-ado-map.json` exists
2. If it does, look up the current plan file and epic to find the ADO Issue ID
3. Include `AB#<issueId>` in the commit message
4. Run: `twig set <issueId> --output json` to set the active item
5. Run: `twig state Done --output json` to transition the item
6. If any twig command fails, continue — ADO tracking is advisory, not blocking
```

### Component 4: Git commit-msg Hook

A lightweight hook at `.twig/hooks/commit-msg` (installed by twig's existing git hook infrastructure via `git.hooks.commitMsg: true` in `.twig/config`) that warns when commits lack `AB#` references.

**Behavior**:
- Parse the commit message for `AB#\d+`
- If absent: print a yellow warning to stderr, but exit 0 (advisory, never blocks)
- If present: exit 0 silently

**Why advisory**: Not all commits are plan-related (e.g., doc fixes, config changes, dependency bumps). Blocking would add friction for legitimate non-tracked work.

Note: Twig already has git hook infrastructure. The `.twig/config` has `git.hooks.commitMsg: true`, and the `InitCommand` installs hooks. The commit-msg hook can be added to the existing hook pipeline.

### Component 5: Copilot Instruction Updates

Update `.github/copilot-instructions.md` to document the ADO tracking convention:

```markdown
## ADO Work Item Tracking

Twig development is tracked in `dangreen-msft/Twig` (Basic process: Epic → Issue → Task).
Plan documents map to ADO: one Epic per plan, one Issue per plan epic, optionally one Task per
task row. The mapping is stored in `tools/plan-ado-map.json`.

When committing code that implements plan work:
- Include `AB#<id>` in the commit message (the Issue ID from the mapping file)
- After committing, transition the ADO item: `twig set <id>` then `twig state Done`
- If no mapping exists for the current work, commit normally without AB# reference
```

---

## Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | JSON mapping file, not YAML | PowerShell has native JSON support. No `powershell-yaml` dependency. |
| DD-02 | Epic → Issue → Task hierarchy | Matches the Basic process template exactly. Epics group plans, Issues track epic-level progress, Tasks track implementation detail. |
| DD-03 | Advisory git hook, not blocking | Not all commits are plan-related. Blocking would add friction for config, doc, and dependency work. |
| DD-04 | Committer handles AB# and state transition | The committer agent already owns the git commit. Adding AB# and `twig state` is a natural extension of its responsibilities. |
| DD-05 | JSON mapping lives in `tools/` and is committed | Version-controlled, visible in PRs, diff-friendly. The mapping is project metadata, not secrets. |
| DD-06 | Idempotent seeding | Re-running the seed script skips already-mapped items. Safe to run after adding new epics to a plan. |
| DD-07 | No context-switching | The twig2 repo is permanently on `dangreen-msft/Twig`. No save/restore, no config patching. |

---

## Alternatives Considered

### Alternative A: Twig MCP Server

Build an MCP server exposing twig commands as agent tools.

**Pros**: Native tool calls, structured I/O, richer agent integration.
**Cons**: Significant effort, new project, MCP SDK dependency.
**Decision**: Deferred. Shell commands with `--output json` are sufficient.

### Alternative B: Embed ADO IDs in Plan Documents

Add `<!-- ADO: 42 -->` comments to EPIC headings.

**Pros**: Single source of truth.
**Cons**: Conductor's committer would need to preserve annotations; merge conflict risk.
**Decision**: Rejected. Separate mapping file is cleaner.

### Alternative C: YAML Mapping File

Use YAML for the plan-ADO mapping.

**Pros**: More readable for humans.
**Cons**: Requires `powershell-yaml` module (not native).
**Decision**: Rejected. JSON is native to PowerShell and sufficient.

---

## Dependencies

| Dependency | Purpose | Status |
|-----------|---------|--------|
| `dangreen-msft/Twig` ADO project | Hosts work items | Initialized, Basic template, Sprint 1 |
| `az cli` authentication | Twig uses az cli for ADO API | Configured in `.twig/config` |
| Conductor v0.1.5 | Workflow orchestration | Installed |
| `twig` CLI | Seed, set, state commands | Built locally |

## Sequencing

1. Seed script created → 2. ADO items seeded → 3. Conductor updated → 4. Git hook installed → 5. Instructions updated

---

## Impact Analysis

### Files Affected

| File | Change |
|------|--------|
| `tools/seed-from-plan.ps1` | **New** — seeding script |
| `tools/plan-ado-map.json` | **New** — mapping file (generated by seed script) |
| `.github/skills/octane-workflow-implement/assets/implement.yaml` | **Modified** — committer prompt: AB# linking + state transition |
| `.github/copilot-instructions.md` | **Modified** — add ADO tracking convention |
| `.twig/config` | **Modified** — already updated (dangreen-msft/Twig context) |

### Backward Compatibility

- **Conductor**: Committer checks for mapping file. If absent, behavior is unchanged.
- **Git hook**: Advisory only. Existing commits and workflows unaffected.
- **Twig source code**: One minor change — `HookHandlerCommand.cs` extended with advisory `AB#` warning in existing commit-msg hook.

---

## Completed Prerequisites

### EPIC-001: Bootstrap & Context Switching

**Status**: DONE

**Completion Date**: 2026-03-27

**Summary**: Implemented `tools/Switch-TwigContext.ps1` with `Save-TwigConfig`, `Restore-TwigConfig`, and `Switch-TwigContext` functions. Three review issues resolved: (1) `$ErrorActionPreference = 'Stop'` moved out of script scope into each function body — dot-sourcing the script no longer pollutes caller's scope; (2) null/empty team comparison normalized using `[string]($config.organization) -eq $Org` cast for all three identity fields; (3) CI test sleep increased from 50ms to 150ms for filesystem mtime reliability. All 21 Pester tests pass including two new regression tests.

**Acceptance Criteria**:
- [x] `Switch-TwigContext.ps1` exists with `-Org`, `-Project`, `-Team` parameters and swaps context in < 1 second
- [x] `Save-TwigConfig` / `Restore-TwigConfig` functions preserve config integrity
- [x] Dot-sourcing does not pollute caller's `$ErrorActionPreference`
- [x] Null `team` field handled correctly in no-op guard comparison
- [x] All 21 Pester tests pass including regression tests for dot-source leakage and null-team no-op

---

## Implementation Plan

### Epic 1: Plan-to-ADO Seeding

**Status**: DONE ✓

**Goal**: Create a script to seed ADO work items from plan documents and persist the mapping.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `seed-from-plan.ps1` with EPIC heading parser supporting both `### Epic N: Title` and `### EPIC-NNN: Title` patterns, plus `- EPIC-NNN:` bullet format | `tools/seed-from-plan.ps1` | DONE |
| E1-T2 | IMPL | Implement hierarchy creation: seed an ADO Epic (per plan), set it as parent, seed Issues (per plan epic) as children | `tools/seed-from-plan.ps1` | DONE |
| E1-T3 | IMPL | Implement `plan-ado-map.json` persistence: load existing, merge new entries, write back with `ConvertTo-Json` | `tools/seed-from-plan.ps1` | DONE |
| E1-T4 | IMPL | Add idempotency check: skip epics that already have a mapping entry | `tools/seed-from-plan.ps1` | DONE |
| E1-T5 | IMPL | Add `--DryRun` flag that previews what would be created without calling twig commands | `tools/seed-from-plan.ps1` | DONE |
| E1-T6 | IMPL | Add `--IncludeTasks` flag for optional Task-level seeding from task table rows | `tools/seed-from-plan.ps1` | DONE |
| E1-T7 | TEST | Seed `twig-interactive-nav.plan.md` — verify ADO Epic + 3 Issues created, mapping file populated | manual | DONE |

**Acceptance Criteria**:
- [x] Workspace bootstrapped: `twig init --org dangreen-msft --project Twig --force` completed successfully (2026-03-27)
- [x] `seed-from-plan.ps1` parses both EPIC heading patterns (verified with dry run on both plans)
- [x] ADO Epic created per plan, Issues created per plan epic, as children (Epic #1252, #1256)
- [x] `plan-ado-map.json` persisted with correct IDs
- [x] Re-running the script is safe (idempotent — verified with second run on interactive-nav)
- [x] `--DryRun` shows planned operations without side effects

---

### Epic 2: Conductor Committer Integration

**Status**: DONE ✓

**Completion Date**: 2026-03-27

**Goal**: Teach the committer agent to link commits to ADO and transition work item state.

**Prerequisites**: Epic 1 (mapping file must exist)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Committer prompt updated to read `tools/plan-ado-map.json` (no separate input variable needed — committer reads file directly) | `.github/skills/octane-workflow-implement/assets/implement.yaml` | DONE |
| E2-T2 | IMPL | Update committer agent prompt: read `tools/plan-ado-map.json`, look up ADO Issue ID for current plan + epic, include `AB#<id>` in commit message | `.github/skills/octane-workflow-implement/assets/implement.yaml` | DONE |
| E2-T3 | IMPL | Update committer agent prompt: after commit, run `twig set <issueId> --output json` then `twig state Done --output json` to transition the ADO item | `.github/skills/octane-workflow-implement/assets/implement.yaml` | DONE |
| E2-T4 | IMPL | Add error handling guidance: if twig commands fail, log warning and continue — ADO tracking is advisory | `.github/skills/octane-workflow-implement/assets/implement.yaml` | DONE |
| E2-T5 | TEST | Run conductor on one epic from `twig-interactive-nav.plan.md`, verify commit includes `AB#` and ADO item transitions to Done | manual + `tools/Committer-AdoIntegration.Tests.ps1` | DONE |
| E2-T6 | TEST | Run conductor without mapping file, verify unchanged behavior (no errors, no AB#) | manual + `tools/Committer-AdoIntegration.Tests.ps1` | DONE |

**Acceptance Criteria**:
- [x] Commit messages include `AB#<issueId>` when mapping exists (prompt updated)
- [x] ADO Issue transitions to "Done" after epic completion (prompt updated with twig state Done)
- [x] Conductor works identically when no mapping file exists (conditional check in prompt)
- [x] twig command failures don't block epic execution (advisory error handling in prompt)

---

### Epic 3: Git Guardrails & Instructions

**Status**: DONE ✓

**Goal**: Add advisory git hook and update copilot instructions for ADO tracking awareness.

**Prerequisites**: Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Update `.github/copilot-instructions.md` with ADO tracking convention: hierarchy mapping, `AB#` format, `plan-ado-map.json` location | `.github/copilot-instructions.md` | DONE |
| E3-T2 | IMPL | Extended existing `HookHandlerCommand.HandleCommitMsgAsync` to check for `AB#\d+` and warn to stderr if absent when `plan-ado-map.json` exists. Always exits 0. | `src/Twig/Commands/HookHandlerCommand.cs` | DONE |
| E3-T3 | IMPL | Verified: twig's `HookInstaller` already installs `commit-msg` hook via `twig _hook commit-msg` dispatch. Config `git.hooks.commitMsg: true` is default. | investigation | DONE |
| E3-T4 | TEST | All 19 existing HookHandlerCommand tests pass after AB# warning addition | `tests/Twig.Cli.Tests/Commands/HookHandlerCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] Copilot instructions document the ADO tracking convention (added ## ADO Work Item Tracking section)
- [x] Git commit-msg hook warns on missing `AB#` (advisory, never blocks) — extends existing HookHandlerCommand
- [x] Hook integrates with twig's existing git hook infrastructure (uses existing `twig _hook commit-msg` dispatch)

---

### Epic 4: Seed All Incomplete Work

**Status**: DONE ✓

**Goal**: Populate ADO with work items for all incomplete plans.

**Prerequisites**: Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Seed `twig-interactive-nav.plan.md`: ADO Epic #1252 + Issues #1253, #1254, #1255 | `tools/plan-ado-map.json` | DONE |
| E4-T2 | IMPL | Seed `twig-on-twig-integration.plan.md`: ADO Epic #1256 + Issues #1257, #1258, #1259, #1260 | `tools/plan-ado-map.json` | DONE |
| E4-T3 | TEST | Verify ADO board shows all seeded items with correct hierarchy | manual | TO DO |
| E4-T4 | TEST | Run `twig ws --output json` and verify seeded items appear in workspace | manual | TO DO |

**Acceptance Criteria**:
- [x] ADO Epics exist for: Interactive Navigation (#1252), Twig-on-Twig Integration (#1256)
- [x] ADO Issues exist for each plan epic within those Epics (7 Issues total)
- [x] `plan-ado-map.json` contains all mappings (2 plans, 7 epics)
- [ ] Items visible on ADO board with correct parent-child relationships (manual verification needed)

---

## References

- [Twig PRD](twig.prd.md) — Product requirements document
- [Twig Requirements](twig.req.md) — Functional and non-functional requirements
- [Conductor implement.yaml](../../.github/skills/octane-workflow-implement/assets/implement.yaml) — Implementation workflow (6 agents)
- [Copilot Instructions](../../.github/copilot-instructions.md) — Global coding conventions and privacy constraints
- [Azure DevOps REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items) — Work items API
