---
name: twig-sdlc
description: Run the end-to-end SDLC workflow for twig development. Takes an ADO work item or natural language prompt through planning, implementation, PR lifecycle, and close-out via Conductor multi-agent orchestration. Activate when asked to implement a feature end-to-end, run the full SDLC pipeline, or orchestrate planning through delivery.
user-invokable: true
---

# End-to-End SDLC Workflow

Orchestrated SDLC pipeline powered by the `conductor` skill. Takes an ADO work item (Epic or Issue) or a natural language prompt and drives it through planning, implementation, code review, PR management, and close-out — all via multi-agent orchestration.

> **This workflow is long-running** — typically 30-120+ minutes depending on scope. **Always launch with `conductor run ... --web`** (no `--silent`) in a hidden window — this captures full agent output in logs for post-mortem analysis while the web dashboard provides real-time monitoring. **Do NOT use `--web-bg`** — it does not work correctly; always use `--web`.

> **Always launch detached** — use `Start-Process -WindowStyle Hidden` so conductor
> survives if the parent session drops and doesn't clutter the terminal.
> Non-detached async shells die when the Copilot CLI session reconnects or terminates.

## Workflows

All workflows are registered in the `twig` conductor registry. Use short names — no absolute paths needed.

> **Workflow source**: [PolyphonyRequiem/twig-conductor-workflows](https://github.com/PolyphonyRequiem/twig-conductor-workflows)
> Install: `conductor registry add twig --source github://PolyphonyRequiem/twig-conductor-workflows`
> Update: `conductor registry update twig`

| Workflow | Registry Name | Purpose | Key Inputs |
|----------|---------------|---------|------------|
| Planning only | `twig-sdlc-planning@twig` | Recursive planner: architect + review + seed + per-issue task planning | `work_item_id` or `prompt`, `intent` |
| Implementation only | `twig-sdlc-implement@twig` | Coding, review, PR lifecycle | `work_item_id`, `intent` |
| Full (composite) | `twig-sdlc-full@twig` | Planning → implementation → close-out → retrospective | `work_item_id` or `prompt`, `intent` |

### Workflow Inputs

| Input | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `work_item_id` | string | conditional | — | ADO work item ID (Epic, Issue, or Task). Required for `resume` and `redo`. |
| `prompt` | string | conditional | `""` | Natural language description. Creates a new Epic. Forces `intent=new`. |
| `intent` | string | no | `resume` | User intent: `new` (fresh start), `redo` (delete and redo), `resume` (pick up where left off). |
| `pr_owner` | string | **yes** | — | GitHub owner (org or user) of the PR target repository. For twig: `PolyphonyRequiem`. |
| `pr_repo_name` | string | **yes** | — | GitHub repository name (without owner) of the PR target. For twig: `twig`. |
| `gh_user` | string | no | `""` | Explicit `gh` CLI user identity. Pinned via `$env:GH_CONDUCTOR_USER`. Empty = use active gh account. |

> **`pr_owner` and `pr_repo_name` are required.** They tell the workflow which GitHub
> repository to target for PR creation, review, and merge. Omitting them causes the
> workflow to fail at the PR lifecycle stage. For twig, always pass
> `pr_owner=PolyphonyRequiem` and `pr_repo_name=twig`.

> **`skip_plan_review` is removed** — use `intent=resume` instead (default behavior).
> For new runs from a prompt, `intent=new` is inferred automatically.

## Workflow Metadata

All metadata is passed dynamically via `--metadata` / `-m` flags at invocation time.
The workflow YAMLs contain no metadata — the invoking agent resolves all values.

| Field | Example | Description |
|-------|---------|-------------|
| `tracker` | `ado` | Work item tracking system |
| `project_url` | `https://dev.azure.com/dangreen-msft/Twig` | ADO project URL |
| `git_repo` | `C:\Users\dangreen\projects\twig2` | Originating git repo path |
| `workitem_id` | `1842` | Target work item ID (numeric) |
| `worktree_name` | `twig2-1842` | Git worktree directory name |
| `cwd` | `C:\Users\dangreen\projects\twig2-1842` | Worktree working directory |

> **All values must be resolved** — templates with `{braces}` are skipped by the dashboard.

## Quick Reference

**Always run in a dedicated worktree** — never on `main` or your working tree. Name the
worktree `twig2-<ID>` based on the work item ID.

```powershell
# 1. Create a worktree for the work item
git worktree add -b sdlc/<ID> ../twig2-<ID> main
cd ../twig2-<ID>

# 2. Restore dependencies (worktrees don't share NuGet packages)
dotnet restore

# 3. Copy .twig workspace and set context to the target work item
Copy-Item -Recurse ../twig2/.twig .twig
twig set <ID>
twig sync

# 4. Run the full SDLC — default intent is "resume"
conductor run twig-sdlc-full@twig --input work_item_id=<ID> --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=<ID> -m worktree_name=twig2-<ID> -m cwd=C:\Users\dangreen\projects\twig2-<ID> --web

# For a brand new work item (no prior work):
conductor run twig-sdlc-full@twig --input work_item_id=<ID> --input intent=new --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=<ID> -m worktree_name=twig2-<ID> -m cwd=C:\Users\dangreen\projects\twig2-<ID> --web

# To redo from scratch (deletes existing children/branches):
conductor run twig-sdlc-full@twig --input work_item_id=<ID> --input intent=redo --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=<ID> -m worktree_name=twig2-<ID> -m cwd=C:\Users\dangreen\projects\twig2-<ID> --web
```

## Launching Multiple Runs

When running multiple work items, use **discrete worktrees** and launch **10 seconds apart**.

### Option A: Direct launch with logging (recommended)

Simpler approach — launches conductor directly with `Tee-Object` for log capture.
Dashboard URLs are written to `conductor.log` in each worktree.

```powershell
# Create worktrees (NEVER on main — always a dedicated branch)
git worktree add -b sdlc/1673 ../twig2-1673 main
git worktree add -b sdlc/1782 ../twig2-1782 main

# Launch 10s apart with log capture
$ids = 1673, 1782
foreach ($id in $ids) {
    $wt = "C:\Users\dangreen\projects\twig2-$id"
    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-Command",
        "cd $wt; conductor run twig-sdlc-full@twig --input work_item_id=$id --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=$id -m worktree_name=twig2-$id -m cwd=$wt --web 2>&1 | Tee-Object -FilePath $wt\conductor.log" `
        -WindowStyle Hidden -PassThru | ForEach-Object { "Launched #$id — PID $($_.Id)" }
    if ($id -ne $ids[-1]) { Start-Sleep -Seconds 10 }
}

# Retrieve dashboard URLs after ~15s
Start-Sleep -Seconds 15
foreach ($id in $ids) {
    Get-Content "C:\Users\dangreen\projects\twig2-$id\conductor.log" |
        Select-String "Dashboard:" | Select-Object -First 1
}
```

### Option B: Job Object wrapper (orphan cleanup)

Use `tools/run-conductor.ps1` when orphaned processes are a concern (MCP servers, test
runners surviving after conductor exits). The wrapper creates a Windows Job Object that
kills all child processes when conductor exits.

> **⚠️ Argument quoting is critical** — the `-Arguments` value must be wrapped in escaped
> quotes so `Start-Process` passes it as a single token to the script parameter.

```powershell
$ids = 1673, 1782
foreach ($id in $ids) {
    $wt = "C:\Users\dangreen\projects\twig2-$id"
    $args = "run twig-sdlc-full@twig --input work_item_id=$id --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=$id -m worktree_name=twig2-$id -m cwd=$wt --web"
    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-File",
        "tools\run-conductor.ps1",
        "-WorkingDirectory", "`"$wt`"",
        "-Arguments", "`"$args`"" `
        -WindowStyle Hidden
    if ($id -ne $ids[-1]) { Start-Sleep -Seconds 10 }
}
```

The 10-second stagger avoids MCP server port collisions and rate-limit spikes.

> **Always share the dashboard URL** — after launching, provide the user the web dashboard URL from terminal output.

### Worktree Cleanup

After runs complete, clean up worktrees:

```bash
# Remove worktrees and branches
git worktree remove --force ../twig2-1673
git branch -D sdlc/1673

# If directories are locked, kill lingering processes first:
dotnet build-server shutdown
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'twig2-\d+' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

## Phases

### Phase 0: Preflight Validation

Validates external dependencies before expensive LLM planning begins. Catches broken
auth, missing tooling, and connectivity failures early — saving minutes of wasted Opus
inference that would otherwise fail opaquely in a downstream agent.

#### Entry Points

| Script | Workflow | Purpose | Budget |
|--------|----------|---------|--------|
| `preflight-check.ps1` | `twig-sdlc-full.yaml` | Full validation (12 checks) for apex SDLC entry | ≤10 seconds |
| `preflight-lite.ps1` | `twig-sdlc-planning.yaml`, `twig-sdlc-implement.yaml` | Lightweight validation (3 checks) for sub-workflow re-entry | ≤5 seconds |

**Full preflight** runs as the `preflight_check` script node in `twig-sdlc-full.yaml`.
On failure, it routes to the `preflight_gate` human gate where the user can retry,
proceed anyway (advisory-only failures), or abort.

**Lite preflight** runs as the `preflight_lite` script node in sub-workflows. This
catches the most common failures (gh auth, git repo, ADO connectivity) without
repeating the full 12-check suite. On failure, it routes to `preflight_lite_gate`.

#### Required vs. Advisory Checks

Checks are classified into two categories:

- **Required** — failure sets `ready=false` and blocks the workflow. The human gate
  fires with remediation instructions. The user must fix the issue before proceeding.
- **Advisory** — failure sets `has_warnings=true` but does NOT block. The workflow
  continues with a warning. Advisory checks predict downstream issues but are not
  hard blockers (e.g., missing `twig-mcp` binary may cause MCP server startup failure,
  but the workflow can still run).

#### Full Preflight Checks (`preflight-check.ps1`)

| # | Check | Category | What It Validates |
|---|-------|----------|-------------------|
| 1 | `gh_auth` | Required | `gh api user` — GitHub CLI is authenticated |
| 2 | `gh_push` | Required | `gh api repos/:slug` — push access on the repository |
| 3 | `ado_access` | Required | `twig sync` + `twig set <id>` — ADO reachable, work item exists |
| 4 | `twig_state` | Required | `twig state --help` — process config is loaded |
| 5 | `dotnet_sdk` | Required | `dotnet --version` — .NET SDK is installed |
| 6 | `git_status` | Required | `git branch --show-current` — git is functional |
| 7 | `gh_default_repo` | Required | `gh repo set-default` — default repo set for `gh pr create` |
| 8 | `conductor_version` | Advisory | `conductor --version` — conductor CLI is installed |
| 9 | `twig_mcp_binary` | Advisory | `Get-Command twig-mcp` — twig-mcp binary is in PATH |
| 10 | `twig_config` | Advisory | `Test-Path .twig/` — workspace config directory exists |
| 11 | `network_ado` | Advisory | HTTP HEAD to `dev.azure.com` (3s timeout) |
| 12 | `network_github` | Advisory | HTTP HEAD to `github.com` (3s timeout) |

#### Lite Preflight Checks (`preflight-lite.ps1`)

| # | Check | Category | What It Validates |
|---|-------|----------|-------------------|
| 1 | `gh_auth` | Required | `gh api user` — GitHub CLI is authenticated |
| 2 | `git_repo` | Required | `git rev-parse --git-dir` — running inside a git repo |
| 3 | `ado_access` | Required | `twig set <id>` — ADO reachable, work item exists |

All lite checks are required — no advisory category in lite mode.

#### Output Schema

Both scripts output the same JSON structure for gate template compatibility:

```json
{
  "ready": true,
  "has_warnings": false,
  "required_checks": [
    { "name": "gh_auth", "passed": true, "detail": "Logged in as user", "category": "required" }
  ],
  "advisory_checks": [
    { "name": "conductor_version", "passed": false, "detail": "...", "remediation": "Install conductor CLI", "category": "advisory" }
  ],
  "failed_count": 0,
  "warning_count": 1,
  "summary": "All required checks passed. 1 advisory warning."
}
```

| Field | Type | Description |
|-------|------|-------------|
| `ready` | boolean | `true` only when ALL required checks pass |
| `has_warnings` | boolean | `true` when any advisory check fails |
| `required_checks` | array | Results for each required check |
| `advisory_checks` | array | Results for each advisory check (empty in lite mode) |
| `failed_count` | number | Count of failed required checks |
| `warning_count` | number | Count of failed advisory checks |
| `summary` | string | Human-readable summary for the gate prompt |

#### Remediation Steps

**Required check failures** — workflow is blocked until resolved:

| Check | Remediation |
|-------|-------------|
| `gh_auth` | Run `gh auth login` to re-authenticate |
| `gh_push` | Run `gh auth switch --user <owner>` if wrong account is active |
| `ado_access` | Run `az login` or set `TWIG_PAT` env var; run `twig sync` to refresh cache |
| `twig_state` | Run `./publish-local.ps1` to rebuild twig; run `twig sync` to refresh process config |
| `dotnet_sdk` | Install the .NET SDK from https://dot.net |
| `git_status` | Ensure `git` is installed and you are inside a git repository |
| `gh_default_repo` | Run `gh repo set-default <owner/repo>` |
| `git_repo` (lite) | `cd` into a git repository before running the workflow |

**Advisory check failures** — workflow proceeds with warnings:

| Check | Remediation |
|-------|-------------|
| `conductor_version` | Install conductor CLI or add it to PATH |
| `twig_mcp_binary` | Run `./publish-local.ps1` to build and deploy twig-mcp |
| `twig_config` | Run `twig init` or clone a repo with `.twig/` checked in |
| `network_ado` | Check network connectivity and proxy settings |
| `network_github` | Check network connectivity and proxy settings |

#### Workflow Routing

```
twig-sdlc-full.yaml:
  preflight_check ──→ ready=true ──→ state_detector (continue)
       │
       └── ready=false ──→ preflight_gate (human gate)
                               ├─ retry → preflight_check (loop)
                               ├─ proceed anyway (advisory-only)
                               └─ abort → $end

twig-sdlc-planning.yaml / twig-sdlc-implement.yaml:
  preflight_lite ──→ ready=true ──→ duplicate_check (continue)
       │
       └── ready=false ──→ preflight_lite_gate (human gate)
                               ├─ retry → preflight_lite (loop)
                               └─ abort → $end
```

### Phase 1: Intake (1 agent)

Reads an existing ADO work item or creates a new Epic from a prompt. Gathers context (title, description, existing child items) for the planning phase.

### Phase 2: Planning (4 agents + 1 parallel group + 2 human gates)

```
architect → open_questions_gate (human gate, conditional)
  → parallel(technical_reviewer, readability_reviewer)
  → review_router → plan_approval (human gate)
```

- **architect** (Opus 1M) — researches codebase, creates `.plan.md` with PR groupings
- **open_questions_gate** — human gate triggered when architect has blocking open questions (moderate+ severity)
- **technical_reviewer** (Opus 1M) + **readability_reviewer** (Sonnet) — parallel review, both must score ≥90
- **review_router** — checks scores, loops to architect or skips approval for trivial plans
- **plan_approval** — human gate with approve/revise/reject options

### Phase 3: Work Tree Seeding (1 agent)

Creates ADO Issues and Tasks from the plan via `twig seed new`, `twig seed chain`, and `twig seed publish`. Seeds Issues and Tasks matching the plan's ADO hierarchy under the input work item. Uses successor links for execution ordering.

### Phase 4: Implementation (10 agents + 1 human gate)

Two-tier orchestrator split: `pr_group_manager` (outer) owns branch lifecycle and issue
closure; `task_manager` (inner) owns task execution within a PR group. Issues are ONLY
closed after their PR is merged — structurally preventing "code complete ≠ code merged".

```
pr_group_manager ──→ task_manager ──→ coder → reducer_code → task_reviewer
      ▲                   ▲                                       │
      │                   │         ┌── REQUEST_CHANGES ──────────┘
      │                   │         ↓
      │                   │       coder (fix)
      │                   │         │
      │                   ├── task approved, more tasks ──────────┘
      │                   │
      │                   ├── all tasks done → reducer_issue → issue_reviewer
      │                   │                                       │
      │                   │                  ┌── REQUEST_CHANGES ─┘
      │                   │                  ↓
      │                   │         task_manager (fix task)
      │                   │                  │
      │                   ├── issue approved → user_acceptance (human gate)
      │                   │                       │
      │                   │                       ├── accepted/skipped ──┘
      │                   │                       └── changes → task_manager
      │                   │
      │                   └── all issues reviewed → pr_group_ready
      │
      ├── pr_group_ready → reducer_pr → pr_submit → pr_reviewer
      │                                    ▲            │
      │                                    │            ├── APPROVE → pr_merge ──┐
      │                                    │            └── REQUEST_CHANGES ─→ pr_fixer
      │                                    └────────────────────────────────────┘
      ├── pr_merge returns → close issues in this PR group → next PR group
      │
      └── all PR groups done → close_out
```

- **pr_group_manager** (Sonnet) — outer orchestrator: creates branches, closes Issues (only after PR merge), routes PR groups
- **task_manager** (Sonnet) — inner orchestrator: manages task lifecycle within a PR group, routes to coder/reviewers, returns `pr_group_ready` when done (CANNOT close Issues)
- **coder** (Opus 1M) — implements one task at a time with incremental commits and twig notes; has pre-review checklist to avoid round-trips
- **reducer_code** (Sonnet) — simplifies each task's implementation
- **task_reviewer** (Sonnet) — per-task quality gate
- **reducer_issue** (Sonnet) — post-issue code sweep across all tasks in completed issue
- **issue_reviewer** (Opus 1M) — per-issue acceptance criteria, cross-cutting concerns, integration
- **user_acceptance** — human gate, conditional per-issue when user-facing changes are flagged
- **reducer_pr** (Sonnet) — pre-PR reduction sweep (stale references, dead code, cross-task duplication)
- **pr_submit** (Sonnet) — validates build + tests, then creates GitHub PR via `gh pr create`
- **pr_reviewer** (Opus 1M) — holistic PR review
- **pr_fixer** (Sonnet) — addresses PR-level review feedback
- **pr_merge** (Sonnet) — merges and cleans up branch

### Phase 5: Close-out (1 agent)

- **close_out** (Opus 1M) — transitions the work item to Done, produces meta-observations on agent performance and workflow improvements

### Phase 6: Closeout Filing (1 agent)

- **closeout_filer** (Sonnet) — takes the close_out observations and improvement suggestions, creates a tagged ADO Issue for human review. The Issue is tagged `closeout-notes; Needs Review` for easy discovery and triage.

## Agent Summary

| Agent | Model | Role |
|-------|-------|------|
| intake | Sonnet | Read work item / create Epic |
| plan_reader | Sonnet | Read existing approved plan (bypass planning) |
| architect | Opus 1M | Design + implementation plan |
| open_questions_gate | Human Gate | Blocking open questions during planning |
| technical_reviewer | Opus 1M | Technical accuracy review |
| readability_reviewer | Sonnet | Clarity and structure review |
| review_router | Sonnet | Score checking + routing |
| plan_approval | Human Gate | Approve / revise / reject plan |
| work_tree_seeder | Sonnet | Create ADO Issues + Tasks |
| pr_group_manager | Sonnet | Outer orchestrator: branch + issue closure (after PR merge) |
| task_manager | Sonnet | Inner orchestrator: task lifecycle + routing |
| coder | Opus 1M | Task implementation |
| reducer_code | Sonnet | Per-task code simplification |
| task_reviewer | Sonnet | Per-task quality gate |
| reducer_issue | Sonnet | Post-issue cross-task code sweep |
| issue_reviewer | Opus 1M | Per-issue acceptance criteria check |
| user_acceptance | Human Gate | Conditional per-issue acceptance |
| reducer_pr | Sonnet | Pre-PR reduction sweep |
| pr_submit | Sonnet | Build validation + GitHub PR creation |
| pr_reviewer | Opus 1M | Holistic PR review |
| pr_fixer | Sonnet | PR fix cycle |
| pr_merge | Sonnet | Merge + cleanup |
| close_out | Opus 1M | Epic completion + observations |
| closeout_filer | Sonnet | File observations as tagged ADO Issue |

## Plan File Frontmatter

Every `.plan.md` file in `docs/projects/` **must** include YAML frontmatter with three required fields:

```yaml
---
work_item_id: 1858
title: "AzCliAuthProvider Timeout Override"
type: Issue
---
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `work_item_id` | integer | Yes | The ADO work item ID this plan targets. Primary key for plan detection. |
| `title` | string | Yes | Human-readable plan title. Used by `Get-PlanTitle` and for display. |
| `type` | string | Yes | ADO work item type — `Epic`, `Issue`, or `Task`. Indicates the plan's scope level. |

The frontmatter block is placed before the first H1 heading. Existing metadata tables
and blockquotes below the heading are preserved — they serve as human-readable context.

### Plan Detection Behavior

The `plan_detector` agent uses frontmatter to match plans to work items:

1. **Primary (frontmatter match)**: Parses each `docs/projects/*.plan.md` file's YAML
   frontmatter and compares `work_item_id` to the requested work item ID. An exact
   numeric match is required — this eliminates false positives from content references.
2. **Fallback (content search)**: If no frontmatter match is found, falls back to
   searching file content for the work item ID. This is a legacy heuristic with lower
   confidence and is subject to false positives (e.g., a parent Epic's plan listing
   a child Issue ID).

### Architect Agent Requirements

When generating a new plan, the **architect** agent must:
- Include the YAML frontmatter block as the very first content in the file
- Set `work_item_id` to the target work item's numeric ID (no `#` prefix)
- Set `title` to the plan's goal or title
- Set `type` to the work item type (`Epic`, `Issue`, or `Task`)

Plans without frontmatter will still be detected via content search fallback, but
frontmatter is the canonical mechanism and must be included in all new plans.

## When to Use

- **Full feature implementation** — "implement this Epic end-to-end"
- **New feature from scratch** — "build a twig export command"
- **Structured pipeline** — when you want planning, review gates, and PR management automated

## When NOT to Use

- **Quick fixes** — single-file changes don't need a 5-phase pipeline

## Known Issues & Mitigations

### Agent Session Stall ("did not respond for 90s")

**Symptom:** Conductor logs show `Idle Recovery 1/5 - last: tool 'powershell'` (or `view`/`grep`),
followed by `Session appears stuck after 5 recovery attempts`. The workflow terminates with a
`ProviderError`.

**Root Cause:** The LLM API fails to return a streaming response after tool results are delivered.
The tool executes successfully and returns output, but the subsequent `Processing (turn N)...`
step hangs indefinitely. This is an API-level connection drop or silent timeout — NOT a tool
execution hang.

**Observed Pattern (from logs across polyphony-2581, twig2-2587, twig2-2541):**

| Agent | Hung at Turn | Last Tool | Model |
|-------|-------------|-----------|-------|
| `pr_finalizer` | ~10 | `powershell` | sonnet-4.6 |
| `architect` | 7 | `view` | opus-4.6-1m |
| `pr_merge` | 10 | `powershell` | sonnet-4.6 |
| `coder` | 15 | `view`/`grep` | opus-4.6-1m |

**Key observations:**
- Affects **both Opus 1M and Sonnet 4.6** — not model-specific
- Happens at **varying turn depths** (7–15) — not a fixed context cliff
- The 5-retry × 18s = 90s recovery window exhausts without success every time
- Correlates with higher accumulated context (50k–100k input tokens by the failing turn)

**Mitigations when launching:**
1. **Always use `--log-file`** to capture SDK-level errors for post-mortem
2. **Use `intent=resume`** to pick up where a stalled workflow left off — conductor's
   state detection will skip completed phases
3. **Monitor the dashboard** — if an agent shows no progress for >2 minutes, the session
   is likely stalled and will not recover

**Mitigations when resuming after a stall:**
1. Check `conductor.log` tail for which agent stalled and what was last completed
2. Verify work item state with `twig tree` — often the work IS done but the PR/merge step stalled
3. Relaunch with `intent=resume` — the state detector will route past completed work
4. If branches exist with commits but no PRs, create PRs manually with `gh pr create`

**Potential fixes (conductor-level):**
- Add request-level timeouts with automatic retry at the SDK layer
- Checkpoint agent state on each turn so stalled sessions can resume mid-agent
- Reduce context accumulation in long-running agents (summarize earlier turns)
