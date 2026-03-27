# Twig-on-Twig Meta Integration — Using Twig to Track Work on Twig

> **Revision**: 6  
> **Date**: 2026-03-26  
> **Status**: Draft  
> **Revision Notes**: R6: Addressed critical factual issues from technical review (score 87/100). (1) **implement.yaml path corrected**: All references now target `.github/skills/octane-workflow-implement/assets/implement.yaml` — the copy actually loaded by `run-epics.ps1` line 44. The `~/.copilot/skills/` copy has diverged (7 agents including `reducer`) and is NOT used by the orchestration script. (2) **Agent chain corrected**: Removed `reducer` from all agent chain descriptions — it only exists in the `~/.copilot/skills/` copy (line 273). The `.github/skills/` version used by `run-epics.ps1` has 6 agents: `epic_selector → coder → epic_reviewer → committer → plan_reviewer → fixer`. (3) **ConvertFrom-Yaml 'natively' claim removed**: `ConvertFrom-Yaml` requires the third-party `powershell-yaml` module; it is not native to any PowerShell version. Added explicit prerequisite in EPIC-002. (4) **Database sizes corrected**: microsoft/OS/twig.db is 352KB (was 360KB), dangreen-msft/Twig/twig.db is 68KB (was 69KB). (5) **Seed-to-EPIC ordinal mapping fragility fixed**: `seed-from-plan.ps1` now publishes one seed at a time (not `--all`) to avoid ordinal mismatch from stale seeds. (6) **Epic queue count corrected**: 72 completed entries (was '40+'). (7) **Open questions downgraded per user input**: process-specific questions made low severity; design is process-agnostic. (8) **References section corrected**: implement.yaml link now points to `.github/skills/` path.  
> **Previous**: R5: Fixed four factual inaccuracies — `twig state` FormatSuccess output, `twig status` multi-line output, `seed publish --all` batch format, added missing `fixer` agent.

---

## Executive Summary

This design describes how to make the Twig development workflow "twig-aware" — using Twig itself to track work items for the Twig repository. The integration spans four areas: (1) bootstrapping a Twig workspace for twig2 against any ADO org/project (defaulting to the existing `dangreen-msft/Twig` context), (2) bridging plan document tasks to ADO work items via `twig seed` during epic execution, (3) auto-transitioning work item state as conductor marks plan items Done via parameterized `twig state` calls, and (4) surfacing twig workspace context into conductor agent prompts so agents know what they are working on. The approach is deliberately pragmatic and process-agnostic — it composes existing twig CLI commands (`seed new`, `seed publish`, `flow-start`, `flow-done`, `state`, `set --output json`) as shell tool calls within conductor workflow YAML, accepting ADO-specific details (org, project, state names, work item types) as script parameters rather than hard-coding process template assumptions. This requires zero new twig CLI commands and zero MCP server endpoints for the initial integration.

---

## Background

### Current Architecture

**Twig** is a CLI tool that manages Azure DevOps work item context from the terminal. Key capabilities relevant to this design:

| Capability | Command(s) | Output Modes |
|-----------|-----------|-------------|
| Initialize workspace | `twig init --org X --project Y` | human, json |
| View workspace/status | `twig ws --output json`, `twig status --output json` | human, json, minimal |
| Create local seed items | `twig seed new "title"` | human, json |
| Publish seeds to ADO | `twig seed publish --all` | human, json |
| Start work on item | `twig flow-start <id>` | human, json |
| Mark item done | `twig flow-done` | human, json |
| Change state | `twig state <name>` | human, json |
| Save pending changes | `twig save` | human, json |
| Set active context | `twig set <id>` | human, json |

All commands support `--output json` for machine-readable output, making them consumable by scripts and AI agents.

**Multi-Context Workspace**: Twig already supports multiple org/project contexts in a single `.twig/` directory. The `TwigPaths.ForContext()` method derives paths as `.twig/{sanitized-org}/{sanitized-project}/twig.db`. The twig2 repo currently has two contexts:
- `.twig/microsoft/OS/twig.db` (352KB) — CloudVault team work items (active config)
- `.twig/dangreen-msft/Twig/twig.db` (68KB) — Twig project work items (exists but dormant)

The active context is determined by `.twig/config` (a single JSON file), which currently points to `microsoft/OS/CloudVault`. Both SQLite databases coexist independently — only the one matching the config's `organization`/`project` fields is loaded at runtime.

**Conductor Workflows**: The project uses conductor (a YAML-based multi-agent workflow orchestrator) with two key workflows:
- **plan.yaml** (`~/.copilot/skills/`) — `architect → technical_reviewer → readability_reviewer` loop that produces plan documents
- **implement.yaml** (`.github/skills/octane-workflow-implement/assets/implement.yaml`) — `epic_selector → coder → epic_reviewer → committer → plan_reviewer → fixer` loop that implements epics from plan documents. **Note**: A separate copy exists at `~/.copilot/skills/octane-workflow-implement/assets/implement.yaml` with 7 agents (including `reducer`), but `run-epics.ps1` loads from `.github/skills/` (line 44). These two files have diverged and must not be confused.

Both workflows support arbitrary input variables via `{{ workflow.input.variable_name }}` Jinja2 templates, and configure MCP servers (web-search, context7, ms-learn) for agent tool access.

**Epic Queue Runner** (`tools/run-epics.ps1`): Orchestrates sequential epic execution from `tools/epic-queue.txt`. For each entry, it:
1. Runs `conductor run implement.yaml --input plan="..." --input epic="..."`
2. Runs `dotnet test` with up to 3 retries
3. Auto-commits: `feat($planName): implement $EPIC`
4. Marks entry as `# DONE:` in the queue file

The queue file shows 72 completed epics across 15+ plan documents, demonstrating heavy use of this workflow.

**Plan Document Convention**: Plan documents use a task table pattern within EPICs:

```markdown
### EPIC-001: Validation Rules
**Status**: TO DO

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create SeedPublishRules domain model | `src/...` | TO DO |
| E1-T2 | TEST | Unit tests for SeedPublishRules | `tests/...` | TO DO |

**Acceptance Criteria**:
- [ ] SeedPublishRules model exists with Default property
- [ ] All unit tests pass
```

The committer agent marks tasks `DONE`, updates `**Status**: DONE ✓`, and checks off acceptance criteria as it commits code.

### Context That Motivates This Design

The twig project has grown to 72 completed epics tracked via plan documents and the epic queue. All this work is tracked only in markdown files — there are no ADO work items backing the plans. This means:
1. No visibility into twig development progress via ADO boards or queries
2. No ability to use twig's own `flow-start`/`flow-done` lifecycle for twig development
3. Conductor agents have no awareness of the broader work item context they're operating within
4. The `prompt.json` file (generated by `IPromptStateWriter`) always reflects CloudVault work, not twig development work

### Prior Art

The `dangreen-msft/Twig` ADO context already exists (`.twig/dangreen-msft/Twig/twig.db`, 68KB), indicating a previous `twig init --org dangreen-msft --project Twig` was run against this org/project. The config was later switched to `microsoft/OS` for CloudVault work, leaving the Twig context dormant but intact.

---

## Problem Statement

1. **No ADO backing for twig work** — Plan documents define tasks but they have no ADO work item representation. There is no way to track twig development in ADO boards, dashboards, or queries.

2. **Manual context switching** — Switching between CloudVault and Twig contexts requires manually editing `.twig/config`. There is no quick-switch mechanism, and the current config format (3KB JSON with nested type appearances) makes manual editing error-prone.

3. **Conductor is context-blind** — The implement workflow's agents (coder, committer, epic_selector) have no awareness of which ADO work item they are implementing. The `prompt.json` always reflects the last interactive twig session, not the conductor task.

4. **No plan-to-ADO bridge** — When a plan document defines 6 tasks in EPIC-001, there's no mechanism to create corresponding ADO Tasks. When the committer marks them DONE, no ADO state transition occurs.

5. **Epic queue is text-only** — The `epic-queue.txt` tracks progress via `# DONE:` comment prefixes. There's no ADO integration to mirror this state.

---

## Goals and Non-Goals

### Goals

1. **G1**: Bootstrap a working twig workspace for the twig2 repo against a configurable ADO org/project (defaulting to `dangreen-msft/Twig`), verifiable by running `twig ws --output json` and seeing twig work items.
2. **G2**: Define a repeatable, process-agnostic process to create ADO work items from plan document EPIC tasks, using existing `twig seed new` + `twig seed publish`.
3. **G3**: Auto-transition ADO work items when an epic completes successfully in `run-epics.ps1`, using `twig state` with a configurable target state name and `--output json`.
4. **G4**: Surface twig workspace context (active item ID, title, type, state) in conductor agent prompts so agents know what ADO work item they are implementing.
5. **G5**: Integrate twig CLI commands into the `run-epics.ps1` orchestration script as pre/post hooks per epic.
6. **G6**: Require zero new twig CLI commands — compose existing commands only.
7. **G7**: Keep all scripts process-agnostic — ADO org, project, state names, and work item types are parameters, not hard-coded assumptions.

### Non-Goals

- **NG1**: Building an MCP server for twig (future work — would unlock richer agent integration but is not needed for this integration).
- **NG2**: Automatic context switching between ADO contexts at arbitrary times — the user switches manually or via a wrapper script.
- **NG3**: Bi-directional sync between plan document task tables and ADO work items — plan docs remain the source of truth for task definitions; ADO mirrors state at the EPIC level.
- **NG4**: Changing the conductor workflow YAML schema — integration happens via shell commands in `run-epics.ps1` and optional input variables, not new conductor agent types or step primitives.
- **NG5**: Task-level granularity in ADO — each EPIC maps to one ADO work item, not one per task row.
- **NG6**: Hard-coding any specific ADO process template, state names, or work item types — all process-specific details are parameters.

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | A documented bootstrap procedure to initialize/reinitialize a twig context for any ADO org/project (defaulting to `dangreen-msft/Twig`) |
| FR-02 | A reusable script to swap `.twig/config` between known contexts with save/restore semantics, accepting org/project/team as parameters |
| FR-03 | A script or documented process to create EPIC-level ADO work items (via `twig seed new` + `twig seed publish`) from plan documents |
| FR-04 | A mapping file (`plan-ado-map.yaml`) that associates plan file paths and EPIC identifiers to ADO work item IDs |
| FR-05 | `run-epics.ps1` pre-epic hook: load map, switch context, `twig set <ado-id> --output json` → capture as `$twigContext` (single JSON object with work item fields) |
| FR-06 | `run-epics.ps1` post-epic hook (success): `twig state <configurable-state> --output json` to transition ADO state, with state name as a parameter |
| FR-07 | `run-epics.ps1` cleanup: restore original twig context after each epic |
| FR-08 | Conductor `implement.yaml` accepts optional `twig_context` and `twig_ado_id` inputs and injects them into agent system prompts |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Context switch between any two ADO contexts must complete in < 2 seconds |
| NFR-02 | All twig commands used in automation must use `--output json` for reliable parsing |
| NFR-03 | Failure of any twig command in `run-epics.ps1` must not block epic execution (graceful degradation) |
| NFR-04 | All new scripts must be idempotent — safe to re-run without side effects |
| NFR-05 | No script shall hard-code ADO process template details (state names, work item types) — these must be parameters |

---

## Proposed Design

### Architecture Overview

The integration is a thin orchestration layer that composes existing twig CLI commands around the conductor workflow. No changes are made to twig source code.

```
┌─────────────────────────────────────────────────────────┐
│  run-epics.ps1 (enhanced)                               │
│                                                         │
│  For each queued epic:                                  │
│  1. Load plan-ado-map.yaml                              │
│  2. Save current .twig/config                           │
│  3. Switch-TwigContext → $TwigOrg/$TwigProject          │
│  4. twig set <ado-id> --output json → $twigContext    │
│  5. conductor run implement.yaml                        │
│     --input plan="..." --input epic="..."               │
│     --input twig_context="$twigContext"                  │
│     --input twig_ado_id="$adoId"                        │
│  6. dotnet test (existing)                              │
│  7. On success: twig state $DoneState --output json     │
│  8. git commit (existing)                               │
│  9. Restore original .twig/config                       │
└─────────────────────────────────────────────────────────┘
        │                           │
        ▼                           ▼
┌──────────────┐          ┌──────────────────────┐
│ .twig/config │          │ conductor            │
│ (context)    │          │ implement.yaml       │
│              │          │                      │
│ $TwigOrg/   │          │ Agents receive       │
│ $TwigProject │          │ twig_context and     │
│              │          │ twig_ado_id as       │
│              │          │ input variables      │
└──────────────┘          └──────────────────────┘
        │
        ▼
┌──────────────────────────┐
│ Azure DevOps             │
│ $TwigOrg / $TwigProject  │
│                          │
│ Work items ↔ Plan EPICs  │
└──────────────────────────┘
```

### Key Components

#### Component 1: Context Switching Script (`tools/Switch-TwigContext.ps1`)

A PowerShell script that safely swaps the `.twig/config` file between known ADO contexts. The config is a JSON document (currently 3.3KB) containing `organization`, `project`, `team`, and nested settings (auth, defaults, git, flow, display, typeAppearances).

**Design**: Rather than rewriting the full config each time, the script only patches the identity fields (`organization`, `project`, `team`) and preserves all other settings. Most preserved settings like `git.branchTemplate`, `display.icons`, and `flow.autoAssign` are repo-specific. Note: some preserved settings (`typeAppearances`, `defaults.areaPaths`, `defaults.iterationPath`) are org-specific and will be stale after a context switch, but this is benign for `--output json` automation — twig resolves these from the context-specific SQLite database at runtime, not from the config file.

```powershell
# tools/Switch-TwigContext.ps1
param(
    [Parameter(Mandatory)][string]$Org,
    [Parameter(Mandatory)][string]$Project,
    [string]$Team = ""
)

$configPath = Join-Path (Get-Location) ".twig/config"
if (-not (Test-Path $configPath)) {
    Write-Error "No .twig/config found. Run 'twig init' first."
    exit 1
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.organization = $Org
$config.project = $Project
$config.team = $Team
$config | ConvertTo-Json -Depth 20 -Compress:$false | Set-Content $configPath -Encoding UTF8

Write-Host "Switched twig context to $Org/$Project" -ForegroundColor Green
```

**Why not `twig init --force`?** `InitCommand.ExecuteAsync` deletes the context DB, creates a new one, fetches process templates, type appearances, area paths, iterations, and sprint items from ADO. This takes 5-10 seconds and destroys the existing cache. A config patch is instant because both SQLite databases already exist at `.twig/{org}/{project}/twig.db`.

#### Component 2: Plan-to-ADO Mapping File (`tools/plan-ado-map.yaml`)

A YAML file that records the bidirectional mapping between plan document EPICs and ADO work item IDs. This is the source of truth for which ADO item corresponds to which plan epic.

**Schema**:
```yaml
# tools/plan-ado-map.yaml
# Maps plan EPICs to ADO work item IDs in dangreen-msft/Twig
# Format: plan-path → epic-id → ado-work-item-id
plans:
  docs/projects/twig-on-twig-integration.plan.md:
    EPIC-001: 42
    EPIC-002: 43
    EPIC-003: 44
    EPIC-004: 45
```

**Why YAML, not SQLite or JSON?**
- YAML is human-readable and diff-friendly in git
- Simple key-value structure (no nesting beyond 2 levels)
- PowerShell can parse YAML via the `powershell-yaml` module (`ConvertFrom-Yaml` / `ConvertTo-Yaml`). Note: this is NOT a native PowerShell cmdlet — it requires `Install-Module powershell-yaml` as a prerequisite.
- Small file — will never exceed a few hundred lines

#### Component 3: Seeding Script (`tools/seed-from-plan.ps1`)

Parses a plan document to extract EPIC metadata (title from the EPIC heading) and creates a single ADO work item per EPIC via `twig seed new` + `twig seed publish`.

**Key design choice**: One ADO work item per EPIC, not per task row. EPICs are the unit of conductor execution and the unit of ADO state tracking. Individual task rows within an EPIC are tracked only in the plan document.

```powershell
# tools/seed-from-plan.ps1 (pseudocode)
param(
    [Parameter(Mandatory)][string]$PlanFile,
    [string]$ParentId,          # Optional: ADO parent work item ID for all EPICs
    [string]$Org = "dangreen-msft",    # ADO org
    [string]$Project = "Twig",         # ADO project
    [string]$Team = ""                 # ADO team (optional)
)

# 1. Parse plan file to extract EPIC headings
#    Pattern: ### EPIC-NNN: Title
$epics = Select-String -Path $PlanFile -Pattern '### (EPIC-\d+): (.+)' |
    ForEach-Object { @{ Id = $_.Matches[0].Groups[1].Value; Title = $_.Matches[0].Groups[2].Value } }

# 2. Switch to twig context
. .\tools\Switch-TwigContext.ps1 -Org $Org -Project $Project -Team $Team

# 3. If parent ID provided, set context
if ($ParentId) { twig set $ParentId }

# 4. Create and publish seeds one at a time to avoid ordinal mismatch
#    (Publishing one-at-a-time avoids the fragility of `seed publish --all`
#    where stale/orphaned seeds from a prior run would break ordinal mapping)
foreach ($epic in $epics) {
    # seed new --output json returns: {"message": "Created local seed: #-1 EPIC-001: Title (Task)"}
    twig seed new --output json "$($epic.Id): $($epic.Title)"
    Write-Host "Created seed for $($epic.Id): $($epic.Title)"
    
    # Publish this single seed immediately
    $publishResult = twig seed publish --all --output json | ConvertFrom-Json
    # Parse the single result to get the new ADO ID
    $newId = $publishResult.results[0].newId
    # Record mapping: EPIC-ID → ADO work item ID
    # (append to plan-ado-map.yaml)
}

# 5. Update plan-ado-map.yaml with all published IDs
# (Load existing YAML, merge new entries, write back)
```

#### Component 4: Enhanced run-epics.ps1

The existing script is enhanced with twig lifecycle hooks. All additions are wrapped in a `$UseTwig` guard that defaults to `$true` but degrades gracefully if twig commands fail or the map file is absent.

**New parameters**:
```powershell
param(
    # ... existing params ...
    [switch]$NoTwig,          # Disable twig integration entirely
    [string]$MapFile = "tools/plan-ado-map.yaml",
    [string]$TwigOrg = "dangreen-msft",    # ADO org for twig tracking
    [string]$TwigProject = "Twig",         # ADO project for twig tracking
    [string]$TwigTeam = "",                # ADO team (optional)
    [string]$DoneState = "Resolved"        # State to transition to on success
)
```

**Per-epic lifecycle additions**:

```
BEFORE conductor:
  1. $adoMap = Load-PlanAdoMap $MapFile  (returns $null if file missing)
  2. $adoId = $adoMap[$entry.Plan][$entry.Epic]  (returns $null if no mapping)
  3. If $adoId:
     a. Save-TwigConfig  (backup current .twig/config)
     b. Switch-TwigContext -Org $TwigOrg -Project $TwigProject -Team $TwigTeam
     c. $twigContext = (twig set $adoId --output json | Select-Object -Last 1)
        # twig set outputs FormatWorkItem as a single JSON object: {id, title, type, state, ...}
        # Select-Object -Last 1 guards against any preceding FormatInfo lines on cache miss
  4. Pass to conductor: --input twig_context="$twigContext" --input twig_ado_id="$adoId"

AFTER conductor + tests (on success):
  5. If $adoId:
     a. twig state $DoneState --output json
        # Returns: {"message": "#42 → Resolved"} (FormatSuccess, not structured work item JSON)
     b. Write-Host "ADO #$adoId → $DoneState"

ALWAYS (cleanup):
  6. Restore-TwigConfig  (restore original .twig/config)
```

**Graceful degradation**: Every twig command is wrapped:
```powershell
try {
    twig set $adoId --output json 2>&1 | Out-Null
} catch {
    Write-Host "  ⚠ twig set failed: $_" -ForegroundColor Yellow
    # Continue without twig integration for this epic
}
```

#### Component 5: Conductor Context Injection

The `implement.yaml` workflow is modified to accept optional `twig_context` and `twig_ado_id` inputs. When present, they're injected into the `epic_selector`, `coder`, and `committer` agent system prompts via Jinja2 conditional blocks.

**Input declaration** (in workflow `inputs` section — if supported, otherwise as a recognized key in `workflow.input`):
```yaml
inputs:
  plan:
    type: string
    required: true
  epic:
    type: string
    required: false
  twig_context:
    type: string
    required: false
    description: "JSON blob from 'twig set <id> --output json' — ADO work item context"
  twig_ado_id:
    type: string
    required: false
    description: "ADO work item ID (passed separately to avoid needing fromjson Jinja2 filter)"
```

**Prompt injection** (in epic_selector's system prompt):
```jinja2
{% if workflow.input.twig_context %}

## Azure DevOps Work Item Context
This epic is tracked as an ADO work item. Context:
```json
{{ workflow.input.twig_context }}
```
When referencing this work in commit messages, use the ADO link format: AB#{{ workflow.input.twig_ado_id | default("unknown") }}.
{% endif %}
```

**Committer prompt injection**:
```jinja2
{% if workflow.input.twig_ado_id %}
Include `AB#{{ workflow.input.twig_ado_id }}` in the commit message to link it to the Azure DevOps work item.
{% endif %}
```

> **Note**: The ADO ID is passed as a separate `twig_ado_id` input rather than extracted from `twig_context` JSON via `fromjson`, because conductor's Jinja2 environment supports `json`, `upper`, `lower`, `default`, `length`, `join` filters but not `fromjson` (which is an Ansible extension, not standard Jinja2).

### Data Flow

#### Flow 1: One-Time Bootstrap

```
User:
  1. Switch-TwigContext -Org $Org -Project $Project [-Team $Team]
  2. twig init --org $Org --project $Project --force
     └─ Creates/refreshes .twig/{org}/{project}/twig.db
     └─ Fetches process template, type appearances, area paths, iterations
     └─ Caches sprint items
  3. twig ws --output json
     └─ Verify workspace is populated
  4. Switch-TwigContext -Org $OriginalOrg -Project $OriginalProject
     └─ Restore original context for day-to-day work
```

#### Flow 2: Seeding EPICs from a Plan Document

```
User runs: tools/seed-from-plan.ps1 -PlanFile "docs/projects/foo.plan.md" [-Org X -Project Y]
  │
  ├─ Parse: ### EPIC-001: Validation Rules → {Id: EPIC-001, Title: "Validation Rules"}
  ├─ Parse: ### EPIC-002: Publish Pipeline → {Id: EPIC-002, Title: "Publish Pipeline"}
  │
  ├─ Switch-TwigContext → $Org/$Project
  │
  ├─ For each EPIC (create + publish one at a time to avoid stale seed contamination):
  │   │
  │   ├─ twig seed new --output json "EPIC-001: Validation Rules"
  │   │   └─ Returns: {"message": "Created local seed: #-1 EPIC-001: Validation Rules (Task)"}
  │   ├─ twig seed publish --all --output json
  │   │   └─ Returns: {"results": [{"oldId": -1, "newId": 42, ...}], ...}
  │   ├─ Record: EPIC-001 → ADO #42
  │   │
  │   ├─ twig seed new --output json "EPIC-002: Publish Pipeline"
  │   │   └─ Returns: {"message": "Created local seed: #-1 EPIC-002: Publish Pipeline (Task)"}
  │   ├─ twig seed publish --all --output json
  │   │   └─ Returns: {"results": [{"oldId": -1, "newId": 43, ...}], ...}
  │   └─ Record: EPIC-002 → ADO #43
  │
  ├─ Update tools/plan-ado-map.yaml:
  │   plans:
  │     docs/projects/foo.plan.md:
  │       EPIC-001: 42
  │       EPIC-002: 43
  │
  └─ Switch-TwigContext → original context
```

#### Flow 3: Conductor Epic Execution with Twig Tracking

```
run-epics.ps1 processes queue entry: "docs/projects/foo.plan.md | EPIC-001"
  │
  ├─ Load plan-ado-map.yaml → EPIC-001 maps to ADO #42
  │
  ├─ Save .twig/config → $savedConfig
  ├─ Switch-TwigContext → $TwigOrg/$TwigProject
  ├─ $twigContext = (twig set 42 --output json | Select-Object -Last 1)
  │   └─ {"id":42,"title":"EPIC-001: Validation Rules","type":"Task","state":"Active",...}
  │   Note: twig set outputs a single FormatWorkItem JSON object (no git context lines).
  │   Select-Object -Last 1 guards against any FormatInfo line on cache miss.
  │
  ├─ conductor run implement.yaml
  │   --input plan="docs/projects/foo.plan.md"
  │   --input epic="EPIC-001"
  │   --input twig_context='{"id":42,...}'
  │   --input twig_ado_id="42"
  │   │
  │   ├─ epic_selector sees: "This epic is tracked as ADO #42"
  │   ├─ coder implements the epic
  │   ├─ committer commits with "EPIC-001: Implement validation rules" + AB#42
  │   └─ conductor exits 0
  │
  ├─ dotnet test → passes ✓
  │
  ├─ twig state $DoneState --output json
  │   └─ {"message": "#42 → Resolved"} (ADO #42 transitions to $DoneState)
  │
  ├─ git add -A && git commit (existing run-epics.ps1 behavior)
  │
  ├─ Mark queue entry: # DONE: docs/projects/foo.plan.md | EPIC-001
  │
  └─ Restore .twig/config from $savedConfig
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Shell commands, not MCP server | MCP server is significant engineering effort. Shell commands with `--output json` achieve the same result for this integration. MCP is a future enhancement (see non-goals). |
| DD-02 | Config patch, not `twig init` for switching | `InitCommand` is destructive (drops DB) and slow (5-10s network calls). Config patching is instant because both databases already exist at their context-specific paths. |
| DD-03 | YAML map file for plan↔ADO mapping | Simple, version-controllable, human-editable. No database schema changes. PowerShell parses YAML via the `powershell-yaml` module (listed as prerequisite). |
| DD-04 | Twig context as conductor input variable | Clean separation — conductor doesn't depend on twig. The JSON blob is opaque context passed through to agent prompts via Jinja2. |
| DD-05 | Graceful degradation when map/twig absent | If no ADO mapping exists for an epic, `run-epics.ps1` proceeds exactly as today. This preserves backward compatibility — existing queue files work without modification. |
| DD-06 | Process-agnostic parameterization | All scripts accept org, project, team, and state names as parameters rather than hard-coding `dangreen-msft/Twig` or `Resolved`. This makes the integration reusable across any ADO project and process template. Defaults to `dangreen-msft/Twig` for the twig repo use case. |
| DD-07 | State transition only on success | Failed epics should not transition ADO state. The item stays in its current state until the epic passes all tests and is committed. |
| DD-08 | One ADO item per EPIC, not per task row | EPICs are the unit of conductor execution and git commit. Task rows are implementation detail tracked in the plan document. ADO items at task-row granularity would create 100+ items with high churn. |
| DD-09 | Restore context after each epic | Prevents context leakage if script is interrupted. Each epic is a self-contained context switch cycle. |

---

## Alternatives Considered

### Alternative A: Twig MCP Server

**Approach**: Build a twig MCP server (stdio-based) that exposes twig commands as MCP tools. Register it in conductor's `runtime.mcp_servers`.

**Pros**:
- Native agent tool calls instead of shell exec
- Structured input/output without JSON parsing
- Richer error handling and streaming
- Agents could interactively query work items during implementation

**Cons**:
- Significant implementation effort (new project, MCP protocol, tool definitions)
- Requires MCP SDK dependency
- Twig is a Native AOT binary — MCP servers are typically Node.js/Python
- Overhead for what is currently 3-4 shell commands

**Decision**: Deferred. Shell commands with `--output json` are sufficient for this integration phase. MCP server becomes valuable when agents need interactive twig operations (browsing work items, navigating trees, creating seeds mid-implementation).

### Alternative B: Conductor Pre/Post Steps

**Approach**: Add `pre_steps` and `post_steps` to the conductor YAML that run twig commands before/after the workflow.

**Cons**:
- Conductor YAML doesn't support arbitrary shell pre/post steps in its current schema
- Would require conductor schema changes (a separate project)
- Tighter coupling between conductor and twig

**Decision**: Rejected. Integration via `run-epics.ps1` wrapper is simpler and doesn't require conductor changes.

### Alternative C: Embed ADO IDs in Plan Document

**Approach**: Instead of a separate YAML map, embed ADO work item IDs directly in plan documents (e.g., `### EPIC-001: Title <!-- ADO: 42 -->`).

**Pros**: Single source of truth, visible in plan document

**Cons**:
- Mutates plan documents beyond conductor's existing DONE marking — risk of merge conflicts
- Conductor's committer agent would need to be taught to preserve ADO ID annotations
- Harder to parse reliably (HTML comments in markdown headings)

**Decision**: Rejected. Separate YAML map is cleaner and doesn't interfere with conductor's plan document management.

### Alternative D: Full twig init per epic

**Approach**: Run `twig init --org dangreen-msft --project Twig --force` before each epic.

**Pros**: Always fresh workspace state

**Cons**: Destructive (drops DB), slow (5-10s of network calls), unnecessary (DB already exists)

**Decision**: Rejected. Config patching is sufficient.

---

## Dependencies

### External Dependencies

| Dependency | Purpose | Status |
|-----------|---------|--------|
| ADO organization (configurable) | Hosts twig work items | `dangreen-msft` workspace DB present; any org works |
| Azure CLI authentication | `twig` uses `az cli` auth method for ADO API calls | Configured in `.twig/config` |
| Conductor CLI | Workflow orchestration | Available in PATH |
| PowerShell YAML module | Parse `plan-ado-map.yaml` | **Required**: `Install-Module powershell-yaml` (not native to any PowerShell version) |

### Internal Dependencies

| Dependency | Purpose |
|-----------|---------|
| `twig` CLI binary | All integration via existing `twig seed new/publish/set/state/status` commands |
| `.twig/{org}/{project}/twig.db` | Workspace database (created via `twig init`) |
| `tools/run-epics.ps1` | Enhanced with twig integration hooks |
| `implement.yaml` conductor workflow | Modified with optional `twig_context` input (`.github/skills/` copy) |

### Sequencing Constraints

1. Target ADO project must exist and be accessible via `az cli` auth (workspace bootstrap)
2. Workspace must be bootstrapped (via `twig init`) before any automation
3. EPIC-level ADO work items must be created (via `seed-from-plan.ps1`) before `run-epics.ps1` can set context and transition state

---

## Impact Analysis

### Components Affected

| Component | Change Type | Impact |
|-----------|------------|--------|
| `tools/run-epics.ps1` | Modified | Additive twig hooks; existing behavior preserved when `--NoTwig` or map absent |
| `.github/skills/octane-workflow-implement/assets/implement.yaml` | Modified | Optional `twig_context` input; no-op when absent |
| `.twig/config` | Modified at runtime | Temporarily patched during epic execution; always restored |

### Backward Compatibility

- **run-epics.ps1**: Without `plan-ado-map.yaml`, all new code paths are skipped. With `--NoTwig`, twig integration is entirely disabled. Existing queue files work unchanged.
- **Conductor**: `{% if workflow.input.twig_context %}` guards mean zero behavioral change when the variable is not passed.
- **Twig source code**: Zero changes. No new commands, no modified commands, no schema changes.
- **Process template independence**: Scripts never assume specific state names or work item types — all are parameterized with sensible defaults.

### Performance Implications

| Operation | Latency | Frequency |
|-----------|---------|-----------|
| Config patch (context switch) | < 100ms | 2× per epic (switch + restore) |
| `twig set <id> --output json` (set + capture context) | < 500ms | 1× per epic |
| `twig state $DoneState --output json` | 1-2s (ADO API call) | 1× per epic (on success) |
| **Total overhead per epic** | **~2-3s** | vs 3-15 min conductor runtime |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ADO org auth expires or becomes inaccessible | Low | High | Document bootstrap; workspace DB can be reconstructed via `twig init --force` |
| Context switch corruption (concurrent twig interactive use during epic run) | Medium | Medium | Context switch is atomic JSON write; document that interactive twig use during `run-epics.ps1` requires care; save/restore protects against partial state |
| `plan-ado-map.yaml` has stale/wrong IDs | Medium | Low | Twig commands fail gracefully (item not found); `run-epics.ps1` continues without twig tracking |
| PowerShell `powershell-yaml` module not installed | Low | Medium | Listed as explicit prerequisite in EPIC-002 task E2-T0. `Install-Module powershell-yaml -Scope CurrentUser` is documented in bootstrap runbook. Script validates availability at startup. |
| Conductor doesn't pass `twig_context` correctly | Low | Low | Conductor already supports arbitrary input variables; test with one epic first |
| Target state name is invalid for the process template | Low | Low | `twig state` already validates transitions and provides clear error messages; script catches and logs the error gracefully |

---

## Open Questions

1. **[Low]** What process template is configured in the target ADO project? This is no longer a blocking question because all scripts accept state names and work item types as parameters. The process template is auto-detected during `twig init` and stored in the workspace database — twig's `StateResolver` handles state name resolution automatically. The user can provide specific state names to `run-epics.ps1 -DoneState` at runtime.

2. **[Low]** What iteration structure exists in the target ADO project? Also no longer blocking — `twig init` auto-detects the current iteration, and if none exist, the workspace will simply show zero items until iterations are configured. The seeding and state-transition flows do not depend on iterations at all.

3. **[Low]** Should `plan-ado-map.yaml` be committed to the repo or kept in `.twig/` (gitignored)? Committing provides version history and team visibility; gitignoring keeps ADO-specific metadata out of source control. Recommendation: commit it (it's a `tools/` artifact like `epic-queue.txt`).

4. **[Low]** Should `run-epics.ps1` restore the original twig context after each epic, or only at script exit? Per-epic restoration adds ~100ms overhead but is safer if the script is interrupted (Ctrl+C, crash). Recommendation: per-epic with a `finally` block.

5. **[Low]** What ADO work item type should represent plan EPICs? Depends on the target project's process template. The `seed-from-plan.ps1` script uses whatever default child type twig resolves for the active parent — no hard-coded type assumption needed. Users can override via `twig seed new --type <type>` if desired.

---

## Implementation Phases

### Phase 1: Bootstrap & Context Switching
Bootstrap a twig workspace for the target ADO org/project and verify it works. Create the context-switching helper script.
**Exit criteria**: `twig ws --output json` returns valid data after context switch; round-trip switch preserves both databases.

### Phase 2: Plan-to-ADO Seeding
Create the `seed-from-plan.ps1` script and `plan-ado-map.yaml` convention. Seed one plan's EPICs as proof-of-concept.
**Exit criteria**: ADO work items exist for at least one plan's EPICs; map file records the ID mappings.

### Phase 3: run-epics.ps1 Integration
Enhance `run-epics.ps1` with parameterized twig context hooks and state transitions. Run one epic end-to-end.
**Exit criteria**: Epic execution sets twig context, passes context to conductor, transitions ADO state on success using parameterized state name, restores context.

### Phase 4: Conductor Prompt Injection
Add `twig_context` input to `implement.yaml` and inject into agent prompts.
**Exit criteria**: Conductor agents see work item context; commit messages reference ADO ID when context is available.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tools/Switch-TwigContext.ps1` | Reusable context-switching helper — patches `.twig/config` org/project/team fields from parameters |
| `tools/seed-from-plan.ps1` | Parses plan EPIC headings and creates ADO work items via `twig seed new` + `twig seed publish` with configurable org/project |
| `tools/plan-ado-map.yaml` | YAML mapping of plan file paths + EPIC IDs to ADO work item IDs |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `tools/run-epics.ps1` | Add twig lifecycle hooks: load map, save/restore context, `twig set`/`twig state`, pass `twig_context` to conductor; all ADO values parameterized |
| `.github/skills/octane-workflow-implement/assets/implement.yaml` | Add optional `twig_context` and `twig_ado_id` inputs; conditional Jinja2 blocks in epic_selector, coder, committer prompts. **Note**: This is the copy loaded by `run-epics.ps1` (line 44). The `~/.copilot/skills/` copy is a separate diverged file and is NOT modified. |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### EPIC-001: Bootstrap & Context Switching

**Status**: IN PROGRESS

**Goal**: Establish a twig workspace for the target ADO org/project and a reliable context-switching mechanism.

**Prerequisites**: None

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `Switch-TwigContext.ps1` that reads `.twig/config` as JSON, patches `organization`/`project`/`team` fields from parameters, writes back with UTF-8 encoding | `tools/Switch-TwigContext.ps1` | IN PROGRESS |
| E1-T2 | IMPL | Add save/restore functions to `Switch-TwigContext.ps1`: `Save-TwigConfig` backs up current config to temp file, `Restore-TwigConfig` restores from backup | `tools/Switch-TwigContext.ps1` | IN PROGRESS |
| E1-T3 | IMPL | Bootstrap workspace: run `twig init --org <org> --project <project> --force` and `twig refresh` to populate the context database (document as a runbook) | manual + documentation | IN PROGRESS |
| E1-T4 | TEST | Verify workspace: `twig ws --output json` against target context returns valid JSON with workspace data | manual verification | IN PROGRESS |
| E1-T5 | TEST | Test context round-trip: switch to context A → `twig status --output json` → switch to context B → `twig status --output json` → verify both return correct context | manual verification | IN PROGRESS |

**Acceptance Criteria**:
- [ ] `Switch-TwigContext.ps1` exists with `-Org`, `-Project`, `-Team` parameters and swaps context in < 1 second
- [ ] `Save-TwigConfig` / `Restore-TwigConfig` functions preserve config integrity
- [ ] `twig ws --output json` returns valid data for the target context
- [ ] Round-trip context switch preserves both databases and active work items

---

### EPIC-002: Plan-to-ADO Seeding

**Goal**: Create tooling to populate ADO work items from plan document EPICs and maintain the mapping.

**Prerequisites**: EPIC-001

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T0 | IMPL | **Prerequisite**: Ensure `powershell-yaml` module is installed (`Install-Module powershell-yaml -Scope CurrentUser`). Add availability check at start of `seed-from-plan.ps1` that errors if module is missing. | `tools/seed-from-plan.ps1` | TO DO |
| E2-T1 | IMPL | Create `plan-ado-map.yaml` with schema documentation comment header and one example entry | `tools/plan-ado-map.yaml` | TO DO |
| E2-T2 | IMPL | Create `seed-from-plan.ps1` with EPIC heading parser: regex `### (EPIC-\d+): (.+)` extracts EPIC ID and title from plan markdown. Accept `-Org`, `-Project`, `-Team` parameters. | `tools/seed-from-plan.ps1` | TO DO |
| E2-T3 | IMPL | Implement one-at-a-time seed+publish loop: for each EPIC, `twig seed new` then `twig seed publish --all` immediately. This avoids the fragility of batch `--all` publish where stale/orphaned seeds from prior runs would break ordinal-to-EPIC mapping. | `tools/seed-from-plan.ps1` | TO DO |
| E2-T4 | IMPL | Parse each single-seed publish result to capture old→new ID mapping (one result per iteration) | `tools/seed-from-plan.ps1` | TO DO |
| E2-T5 | IMPL | Write published IDs to `plan-ado-map.yaml`: load existing YAML via `ConvertFrom-Yaml`, merge new entries, write back via `ConvertTo-Yaml` | `tools/seed-from-plan.ps1` | TO DO |
| E2-T6 | IMPL | Add `--DryRun` flag that shows what would be created without calling twig commands | `tools/seed-from-plan.ps1` | TO DO |
| E2-T7 | TEST | End-to-end: seed one plan's EPICs, verify ADO items created, verify map file updated | manual verification | TO DO |

**Acceptance Criteria**:
- [ ] `powershell-yaml` module availability is validated at script start with clear error message
- [ ] `seed-from-plan.ps1` correctly parses EPIC headings from at least one plan document
- [ ] Seeds are created and published one-at-a-time to avoid stale seed contamination
- [ ] `plan-ado-map.yaml` is updated with published ADO work item IDs
- [ ] `--DryRun` shows planned operations without side effects

---

### EPIC-003: run-epics.ps1 Twig Integration

**Goal**: Enhance the epic queue runner with twig lifecycle hooks for ADO state tracking.

**Prerequisites**: EPIC-001, EPIC-002

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Add `--NoTwig` switch, `--MapFile`, `--TwigOrg`, `--TwigProject`, `--TwigTeam`, and `--DoneState` parameters to `run-epics.ps1` | `tools/run-epics.ps1` | TO DO |
| E3-T2 | IMPL | Add map loading function: parse `plan-ado-map.yaml` into hashtable keyed by `"$plan|$epic"` at script startup | `tools/run-epics.ps1` | TO DO |
| E3-T3 | IMPL | Add pre-epic twig hook: save config, switch context (using parameterized org/project), `$twigContext = (twig set <ado-id> --output json \| Select-Object -Last 1)` to set active item and capture work item JSON in one step | `tools/run-epics.ps1` | TO DO |
| E3-T4 | IMPL | Pass `$twigContext` and `$adoId` to conductor: add `--input twig_context="$twigContext" --input twig_ado_id="$adoId"` to the conductor args array | `tools/run-epics.ps1` | TO DO |
| E3-T5 | IMPL | Add post-epic success hook: `twig state $DoneState --output json` to transition ADO work item state (parameterized) | `tools/run-epics.ps1` | TO DO |
| E3-T6 | IMPL | Add context restore in `finally` block: `Restore-TwigConfig` after every epic (success or failure) | `tools/run-epics.ps1` | TO DO |
| E3-T7 | IMPL | Wrap all twig commands in try/catch for graceful degradation: log warnings, continue without twig tracking | `tools/run-epics.ps1` | TO DO |
| E3-T8 | TEST | Test with mapping: run one epic with ADO mapping, verify context is set and state transitions | manual verification | TO DO |
| E3-T9 | TEST | Test without mapping: run one epic with no map file, verify existing behavior unchanged | manual verification | TO DO |
| E3-T10 | TEST | Test failure case: simulate twig command failure, verify epic still runs via conductor | manual verification | TO DO |

**Acceptance Criteria**:
- [ ] `run-epics.ps1` loads `plan-ado-map.yaml` and resolves ADO IDs for queued epics
- [ ] Twig context is set before conductor runs (verified via captured `$twigContext`)
- [ ] ADO work item state transitions to `$DoneState` on epic success
- [ ] Script completes successfully when `plan-ado-map.yaml` is absent (backward compat)
- [ ] Script completes successfully with `--NoTwig` flag (backward compat)
- [ ] Original twig context is always restored (even on failure/interrupt)
- [ ] All ADO-specific values (org, project, team, state) come from parameters, not hard-coded

---

### EPIC-004: Conductor Context Injection

**Goal**: Make conductor agents aware of the ADO work item they are implementing.

**Prerequisites**: EPIC-003

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Add `twig_context` and `twig_ado_id` to `implement.yaml` inputs section as optional string parameters | `.github/skills/octane-workflow-implement/assets/implement.yaml` | TO DO |
| E4-T2 | IMPL | Add conditional Jinja2 block to `epic_selector` system prompt: display ADO work item ID, title, type, state from `twig_context` JSON | `.github/skills/octane-workflow-implement/assets/implement.yaml` | TO DO |
| E4-T3 | IMPL | Add conditional Jinja2 block to `coder` system prompt: include ADO work item ID for reference | `.github/skills/octane-workflow-implement/assets/implement.yaml` | TO DO |
| E4-T4 | IMPL | Add conditional Jinja2 block to `committer` system prompt: use `twig_ado_id` input to instruct including `AB#<id>` in commit messages for ADO linking (avoids `fromjson` filter) | `.github/skills/octane-workflow-implement/assets/implement.yaml` | TO DO |
| E4-T5 | TEST | Verify agents see work item context: run one epic with `twig_context`, check conductor output for ADO references | manual review | TO DO |
| E4-T6 | TEST | Verify backward compatibility: run one epic without `twig_context`, verify no errors and no ADO references | manual verification | TO DO |

**Acceptance Criteria**:
- [ ] Conductor agents see ADO work item context in their prompts when `twig_context` is provided
- [ ] Commit messages include `AB#<id>` link when `twig_ado_id` is available
- [ ] Workflow executes identically when `twig_context` and `twig_ado_id` are omitted (no errors, no ADO references)

---

## Minimal Twig Commands for Agent Consumption

The following existing twig commands are sufficient for the full integration — **no new commands needed**:

| Command | Purpose in Integration | Output |
|---------|----------------------|--------|
| `twig set <id> --output json` | Set active work item and capture context for conductor input | Single JSON object: `{id, title, type, state, assignedTo, areaPath, iterationPath, isDirty, isSeed, parentId}`. May emit a `{"info": "..."}` line first on cache miss — use `Select-Object -Last 1` to isolate the work item. |
| `twig status --output json` | View current work item with git context (interactive use) | **Multi-line output**: Line 1: work item JSON `{id, title, type, state, ...}`, Line 2: `{"branch": "..."}` (if in git repo), Line 3+: `{"prId": ..., "title": ..., "status": ...}` (per linked PR). **Not used for context capture** in this integration — use `twig set` instead. |
| `twig seed new --output json "title"` | Create local seed work items from plan EPICs | `{"message": "Created local seed: #-1 Title (Type)"}` via FormatSuccess — flat message, not structured seed JSON |
| `twig seed publish --all --output json` | Publish seeds to ADO as real work items | `{"results": [{oldId, newId, title, status, isSuccess, errorMessage, linkWarnings}, ...], "cycleErrors": [], "createdCount": N, "skippedCount": N, "hasErrors": bool}`. Note: per-result items do NOT include `validationFailures` (only the single-publish formatter does). |
| `twig state <name> --output json` | Transition work item on epic completion | `{"message": "#42 → Resolved"}` via FormatSuccess — flat message string, not structured work item JSON |
| `twig ws --output json` | Verify workspace state after bootstrap | JSON workspace with sprint items and seeds |
| `twig refresh --output json` | Re-sync cache from ADO (used in bootstrap) | JSON with item counts |

### Future MCP Server Endpoints (Not In Scope)

If a twig MCP server is built later, these would be the minimal tool definitions:

| Tool | Maps To | Agent Use Case |
|------|---------|---------------|
| `twig_get_status` | `twig status --output json` | Agent queries current context |
| `twig_set_context` | `twig set <id>` | Agent switches work item focus |
| `twig_create_seed` | `twig seed new "title"` | Agent creates work items from plan analysis |
| `twig_publish_seeds` | `twig seed publish --all` | Agent publishes drafts to ADO |
| `twig_transition_state` | `twig state <name>` | Agent marks items done |
| `twig_get_workspace` | `twig ws --output json` | Agent browses available work items |
| `twig_get_tree` | `twig tree --output json` | Agent navigates work item hierarchy |

This is deferred. The shell command approach via `run-epics.ps1` is sufficient for the current integration.

---

## References

- [Twig PRD](twig.prd.md) — Product requirements document for twig CLI
- [Twig Requirements](twig.req.md) — Functional and non-functional requirements
- [Seed Foundation Plan](twig-seed-foundation.plan.md) — Local-first seed architecture
- [Seed Publish Plan](twig-seed-publish.plan.md) — Seed publish pipeline (validate → create → remap → order)
- [Conductor implement.yaml](../../.github/skills/octane-workflow-implement/assets/implement.yaml) — Implementation workflow (the copy used by `run-epics.ps1`)
- [run-epics.ps1](../../tools/run-epics.ps1) — Epic queue runner script
- [epic-queue.txt](../../tools/epic-queue.txt) — Epic queue with 72 completed entries
- [Azure DevOps REST API - Work Items](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items) — ADO API documentation
