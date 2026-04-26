# SDLC Preflight Check — Implementation Plan

---
work_item_id: 2071
title: "SDLC preflight check: validate external dependencies before planning"
type: Issue
status: In Progress
revision: 0
revision_notes: "Initial draft."
---

| Field | Value |
|-------|-------|
| **Work Item** | #2071 |
| **Type** | Issue |
| **Parent** | SDLC Epic (twig-conductor-workflows) |
| **Branch** | `feature/2071-sdlc-preflight` |
| **Author** | Daniel Green |

> **Status**: 🔨 In Progress

## Executive Summary

Add a comprehensive preflight validation system to the SDLC conductor workflows that checks all external dependencies (GitHub CLI, Azure DevOps, .NET SDK, git, conductor, twig-mcp) before expensive LLM planning begins. The system is partially implemented: `preflight-check.ps1` exists with 7 required checks, and `twig-sdlc-full.yaml` already wires it as the entry point with a `preflight_gate` human gate. This plan completes the implementation by adding advisory checks (conductor version, twig-mcp binary, `.twig/` config, network connectivity), introducing a lighter `preflight-lite.ps1` for sub-workflows, separating required vs. advisory check semantics in the output schema, adding Pester tests, and updating documentation.

## Background

### Current State

The SDLC pipeline (`twig-sdlc-full.yaml`) is a multi-agent conductor workflow that orchestrates planning, implementation, PR lifecycle, and close-out. A full run can consume 30+ minutes of Opus-tier LLM time. When external dependencies are broken — `gh` auth expired, wrong GitHub account active, `twig` unable to reach ADO, `dotnet` SDK missing — the workflow silently proceeds until a downstream script agent (`pr_submit`, `branch_pusher`, `pr_merge`) hangs indefinitely or fails opaquely.

### Existing Implementation

A partial preflight system already exists in the conductor workflows registry (`~/.conductor/registries/twig/recursive/`):

| Component | Status | Location |
|-----------|--------|----------|
| `preflight-check.ps1` | ✅ Exists | `recursive/scripts/preflight-check.ps1` |
| `preflight_check` script node | ✅ Wired | `twig-sdlc-full.yaml` (entry_point) |
| `preflight_gate` human gate | ✅ Wired | `twig-sdlc-full.yaml` |
| Advisory checks | ❌ Missing | — |
| Required/advisory separation | ❌ Missing | — |
| Sub-workflow preflight | ❌ Missing | `twig-sdlc-planning.yaml`, `twig-sdlc-implement.yaml` |
| Pester tests | ❌ Missing | — |
| SKILL.md documentation | ❌ Missing | `.github/skills/twig-sdlc/SKILL.md` |

### Current `preflight-check.ps1` Checks (7 checks, all "required")

| # | Check | What It Validates | Sets `$allPassed = $false`? |
|---|-------|-------------------|----------------------------|
| 1 | `gh_auth` | `gh auth status` + `gh api user` | Yes |
| 2 | `gh_push` | `gh api repos/:slug --jq '.permissions.push'` | Yes |
| 3 | `ado_access` | `twig sync` + `twig set <id>` | Yes |
| 4 | `twig_state` | `twig state --help` functional | Yes |
| 5 | `dotnet_sdk` | `dotnet --version` | Yes |
| 6 | `git_status` | `git branch --show-current` | Yes |
| 7 | `gh_default_repo` | `gh repo set-default $repoSlug` | **No** (silent advisory) |

### Existing Script Agent Patterns

All deterministic routing in the SDLC pipeline follows principle P8 (prefer scripts for deterministic logic). Script agents output JSON to stdout with auto-parsing into `output.*` fields. Routing uses Jinja2 conditions on parsed fields:

```yaml
routes:
  - to: state_detector
    when: "{{ output.ready }}"
  - to: preflight_gate
```

Other script patterns in the registry: `detect-state.ps1` (state inspection), `check-branch.ps1` / `check-plan.ps1` / `check-seeding.ps1` (idempotency checks), `load-work-tree.ps1` (ADO tree loading), `post-merge-regression.ps1` (regression testing).

### Sub-Workflow Entry Points

| Workflow | Current Entry Point | Has Preflight? |
|----------|-------------------|----------------|
| `twig-sdlc-full.yaml` | `preflight_check` | ✅ Yes |
| `twig-sdlc-planning.yaml` | `duplicate_check` | ❌ No |
| `twig-sdlc-implement.yaml` | `duplicate_check` | ❌ No |

When sub-workflows are invoked standalone (e.g., `conductor run twig-sdlc-implement@twig --input work_item_id=1234`), they skip the apex preflight entirely.

## Problem Statement

Three gaps remain in the preflight system:

1. **No advisory checks** — The script doesn't validate conductor version, twig-mcp binary existence, `.twig/` workspace configuration, or network connectivity. These aren't hard blockers but strongly predict downstream failures (e.g., missing twig-mcp causes MCP server startup failure; conductor version mismatch causes template bugs).

2. **No required/advisory semantics** — All checks are treated identically. Check 7 (`gh_default_repo`) silently skips on failure without affecting `$allPassed`, but there's no formal advisory category. The `preflight_gate` prompt cannot differentiate between required failures and warnings.

3. **No sub-workflow preflight** — Planning and implementation sub-workflows can be invoked directly, bypassing the apex preflight entirely. A lighter check at sub-workflow entry would catch the most common failures (gh auth, git status, ADO connectivity) without repeating the full 7+ check suite.

## Goals and Non-Goals

### Goals

1. **G1**: Add 4 advisory checks (conductor version, twig-mcp binary, `.twig/` config, network connectivity) to `preflight-check.ps1`
2. **G2**: Separate checks into `required` and `advisory` categories with distinct routing semantics — required failures block, advisory failures warn
3. **G3**: Create `preflight-lite.ps1` for sub-workflows with ≤5 fast checks
4. **G4**: Wire `preflight-lite` into `twig-sdlc-planning.yaml` and `twig-sdlc-implement.yaml`
5. **G5**: Ensure preflight adds no more than 10 seconds to workflow startup
6. **G6**: Add Pester tests for both preflight scripts
7. **G7**: Update `twig-sdlc/SKILL.md` with preflight documentation

### Non-Goals

- **NG1**: Auto-remediation (e.g., running `gh auth login` automatically) — too risky, violates P6
- **NG2**: CI/CD pipeline integration — preflight is for interactive conductor workflows only
- **NG3**: Preflight for `closeout-filing@twig` — lightweight workflow, not worth the overhead
- **NG4**: Conductor health-check daemon — out of scope; preflight is point-in-time validation

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `preflight-check.ps1` outputs JSON with `required_checks` and `advisory_checks` arrays |
| FR-2 | `ready` field is `$true` only when all required checks pass (advisory failures don't block) |
| FR-3 | `has_warnings` field is `$true` when any advisory check fails |
| FR-4 | Advisory checks: conductor version, twig-mcp binary, `.twig/` config, network connectivity |
| FR-5 | `preflight_gate` prompt distinguishes required failures from advisory warnings |
| FR-6 | `preflight-lite.ps1` checks: gh auth, git status, ADO connectivity (via `twig set`) |
| FR-7 | Sub-workflows route through `preflight_lite` → `preflight_lite_gate` → existing entry point |
| FR-8 | GitHub permission check verifies push access via `permissions.push`, not ownership |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Full preflight completes in ≤10 seconds on a healthy system |
| NFR-2 | Lite preflight completes in ≤5 seconds |
| NFR-3 | Network checks use ≤3 second timeout per endpoint |
| NFR-4 | Preflight is idempotent — safe to re-run on resume (P3) |
| NFR-5 | No side effects beyond `gh repo set-default` (which is intentional and idempotent) |

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                     twig-sdlc-full.yaml                         │
│                                                                  │
│  preflight_check ──→ preflight_gate (on required failure)        │
│       │                    │                                     │
│       │ (all required      ├─→ retry (loop back)                 │
│       │  checks pass)      ├─→ proceed anyway                    │
│       ▼                    └─→ abort ($end)                      │
│  state_detector ──→ ...                                          │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                  twig-sdlc-planning.yaml                         │
│                                                                  │
│  preflight_lite ──→ preflight_lite_gate (on failure)             │
│       │                    │                                     │
│       │ (pass)             ├─→ retry                             │
│       ▼                    └─→ abort ($end)                      │
│  duplicate_check ──→ intake ──→ ...                              │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                 twig-sdlc-implement.yaml                         │
│                                                                  │
│  preflight_lite ──→ preflight_lite_gate (on failure)             │
│       │                    │                                     │
│       │ (pass)             ├─→ retry                             │
│       ▼                    └─→ abort ($end)                      │
│  duplicate_check ──→ intake ──→ ...                              │
└──────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Enhanced `preflight-check.ps1`

The existing script is extended with:

**New advisory checks:**

| # | Check | Command | Timeout | Category |
|---|-------|---------|---------|----------|
| 8 | `conductor_version` | `conductor --version` | 3s | Advisory |
| 9 | `twig_mcp_binary` | `Get-Command twig-mcp` | 1s | Advisory |
| 10 | `twig_config` | `Test-Path .twig/` + validate structure | 1s | Advisory |
| 11 | `network_ado` | `Test-Connection dev.azure.com` or HTTP ping | 3s | Advisory |
| 12 | `network_github` | `Test-Connection github.com` or HTTP ping | 3s | Advisory |

**Revised output schema:**

```json
{
  "ready": true,
  "has_warnings": true,
  "required_checks": [
    { "name": "gh_auth", "passed": true, "detail": "Logged in as user", "category": "required" }
  ],
  "advisory_checks": [
    { "name": "conductor_version", "passed": false, "detail": "...", "remediation": "...", "category": "advisory" }
  ],
  "checks": [ /* combined for backward compat */ ],
  "failed_count": 0,
  "warning_count": 1,
  "summary": "All required checks passed. 1 advisory warning."
}
```

**Existing checks reclassified:**
- Checks 1-6 → `required`
- Check 7 (`gh_default_repo`) → `required` (elevated — default repo is essential for `gh pr create`)
- Checks 8-12 → `advisory`

#### 2. New `preflight-lite.ps1`

A minimal, fast script for sub-workflow entry points. Performs only the most critical checks:

| # | Check | What It Validates | Timeout |
|---|-------|-------------------|---------|
| 1 | `gh_auth` | `gh auth status` | 3s |
| 2 | `git_repo` | `git rev-parse --git-dir` | 1s |
| 3 | `ado_access` | `twig set <id>` (work item exists) | 5s |

Total budget: ≤5 seconds. No advisory checks — this is a fast gate.

Output schema matches the full preflight (same structure, fewer checks) so the gate template can be shared.

#### 3. Workflow YAML Changes

**`twig-sdlc-full.yaml`** — Update `preflight_gate` prompt to differentiate required failures from advisory warnings. Add "Proceed with warnings" option when only advisory checks fail.

**`twig-sdlc-planning.yaml`** — Change `entry_point` from `duplicate_check` to `preflight_lite`. Add `preflight_lite` script node and `preflight_lite_gate` human gate before `duplicate_check`.

**`twig-sdlc-implement.yaml`** — Same pattern as planning.

### Data Flow

```
1. User invokes: conductor run twig-sdlc-full@twig --input work_item_id=2071

2. preflight_check (script):
   - Runs preflight-check.ps1 -WorkItemId 2071
   - Outputs JSON: { ready: true/false, has_warnings: true/false, checks: [...] }
   - Routes: ready=true → state_detector | else → preflight_gate

3. preflight_gate (human gate, only on failure):
   - Displays required failures with remediation
   - Displays advisory warnings separately
   - Options: retry | proceed anyway | abort

4. state_detector → ... (existing flow continues)
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Script agent (not LLM agent) for preflight | P8: deterministic logic. Preflight is pure if/else — no judgment needed |
| Single script with all checks (not parallel group) | Simplicity; 10s budget is easily met serially; parallel groups add orchestration overhead |
| Separate `preflight-lite.ps1` (not `--lite` flag) | Cleaner separation; lite script is independently testable; no shared-state complexity |
| Advisory checks don't block `ready` | Avoids false positives — missing conductor binary doesn't mean the workflow can't run (MCP servers handle it) |
| Network checks via HTTP HEAD (not ICMP ping) | `Test-Connection` uses ICMP which is often blocked in corporate networks; HTTP is more reliable |
| `gh_default_repo` elevated to required | `gh pr create` fails without a default repo set; better to catch early |
| Keep `checks` array for backward compatibility | The `preflight_gate` template already references `preflight_check.output.checks` |

## Dependencies

### External Dependencies

| Dependency | Purpose | Risk |
|------------|---------|------|
| Conductor CLI | Workflow execution engine | Low — already required |
| GitHub CLI (`gh`) | Repository operations | Low — checked by preflight itself |
| twig CLI | ADO work item management | Low — checked by preflight itself |
| .NET SDK | Build/test | Low — checked by preflight itself |

### Internal Dependencies

| Dependency | Purpose |
|------------|---------|
| `twig-conductor-workflows` repo | All script and YAML changes live here |
| `.github/skills/twig-sdlc/SKILL.md` | Documentation update in this repo |

### Sequencing Constraints

- No blockers — all changes are additive to existing infrastructure.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Network checks add latency on slow connections | Medium | Low | 3-second timeout per endpoint; advisory-only so doesn't block |
| Conductor JSON auto-parsing regression | Low | High | Test routing with `conductor validate`; keep `exit_code` fallback route |
| `gh repo set-default` side effect on shared machines | Low | Low | Already present in current script; idempotent; uses repo from `git remote` |
| Sub-workflow preflight slows standalone runs | Low | Low | Lite preflight budget is 5s; trivial vs. minutes of LLM time saved |

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| OQ-1 | Should `preflight-lite` skip the `twig sync` that `ado_access` currently does (to save time), or is sync essential for accurate state? | Low | The lite check can use `twig set` without prior sync — `set` reads the local cache which is sufficient for connectivity validation. The full `twig sync` is done by `detect-state.ps1` immediately after preflight anyway. |
| OQ-2 | Should the `network_github` and `network_ado` advisory checks use `Invoke-WebRequest` or `Test-NetConnection`? | Low | `Invoke-WebRequest` with `-Method Head` and `-TimeoutSec 3` is more reliable in corporate environments where ICMP is blocked. |

## Testing and Validation

### Test Strategy

| Test Type | Tool | Location | Coverage |
|-----------|------|----------|----------|
| Unit tests for `preflight-check.ps1` | Pester | `twig-conductor-workflows/tests/preflight-check.Tests.ps1` | All 12 checks, required/advisory separation, edge cases |
| Unit tests for `preflight-lite.ps1` | Pester | `twig-conductor-workflows/tests/preflight-lite.Tests.ps1` | All 3 checks, output schema |
| Workflow validation | `conductor validate` | Manual / CI | YAML syntax, routing reachability |
| Integration smoke test | Manual | Local | End-to-end preflight → state_detector flow |
| Performance validation | Measure-Command | Manual | ≤10s full, ≤5s lite |

### Key Test Cases

1. **All required pass, all advisory pass** → `ready=true`, `has_warnings=false`
2. **All required pass, advisory fail** → `ready=true`, `has_warnings=true`
3. **Any required fail** → `ready=false`
4. **gh auth expired** → `gh_auth` fails with remediation "Run: gh auth login"
5. **Wrong GitHub account** → `gh_push` fails with remediation "Run: gh auth switch"
6. **ADO unreachable** → `ado_access` fails
7. **Work item not found** → `ado_access` fails with specific message
8. **No .twig/ directory** → `twig_config` advisory warning
9. **Conductor not installed** → `conductor_version` advisory warning
10. **Network timeout** → advisory warning, doesn't block

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `recursive/scripts/preflight-lite.ps1` | Lightweight preflight for sub-workflows (3 checks) |
| `tests/preflight-check.Tests.ps1` | Pester tests for full preflight script |
| `tests/preflight-lite.Tests.ps1` | Pester tests for lite preflight script |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `recursive/scripts/preflight-check.ps1` | Add 5 advisory checks (conductor, twig-mcp, twig config, network×2); restructure output with required/advisory arrays; add `has_warnings` and `warning_count` fields |
| `recursive/twig-sdlc-full.yaml` | Update `preflight_gate` prompt template to distinguish required failures from advisory warnings |
| `recursive/twig-sdlc-planning.yaml` | Change `entry_point` to `preflight_lite`; add `preflight_lite` script node and `preflight_lite_gate` human gate |
| `recursive/twig-sdlc-implement.yaml` | Change `entry_point` to `preflight_lite`; add `preflight_lite` script node and `preflight_lite_gate` human gate |
| `.github/skills/twig-sdlc/SKILL.md` (twig repo) | Document preflight phase, checks, remediation steps |

> **Note**: All files except the SKILL.md documentation live in the `twig-conductor-workflows` repository (`~/.conductor/registries/twig/`), not in the `twig` CLI repository.

## ADO Work Item Structure

### Issue #2071 — SDLC preflight check: validate external dependencies before planning

**Goal**: Complete the preflight validation system across all SDLC workflows with required/advisory check separation, sub-workflow coverage, tests, and documentation.

**Prerequisites**: None — all changes are additive.

#### Tasks

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T1 | Add advisory checks and required/advisory output schema to `preflight-check.ps1` | `recursive/scripts/preflight-check.ps1` | ~120 LoC | ✅ DONE |
| T2 | Update `preflight_gate` template in `twig-sdlc-full.yaml` to distinguish required failures from advisory warnings | `recursive/twig-sdlc-full.yaml` | ~30 LoC | TO DO |
| T3 | Create `preflight-lite.ps1` for sub-workflows | `recursive/scripts/preflight-lite.ps1` | ~60 LoC | ✅ DONE |
| T4 | Wire `preflight_lite` into `twig-sdlc-planning.yaml` and `twig-sdlc-implement.yaml` | `recursive/twig-sdlc-planning.yaml`, `recursive/twig-sdlc-implement.yaml` | ~80 LoC | TO DO |
| T5 | Add Pester tests for both preflight scripts | `tests/preflight-check.Tests.ps1`, `tests/preflight-lite.Tests.ps1` | ~200 LoC | ✅ DONE |
| T6 | Update `twig-sdlc/SKILL.md` with preflight documentation | `.github/skills/twig-sdlc/SKILL.md` (twig repo) | ~40 LoC | TO DO |

#### Acceptance Criteria

- [ ] `preflight-check.ps1` validates all 12 checks (7 required + 5 advisory)
- [ ] GitHub permission check verifies **push access** via `permissions.push`, not repo ownership
- [ ] `ready` is `true` only when all required checks pass; advisory failures produce `has_warnings=true`
- [ ] `preflight_gate` displays required failures and advisory warnings in separate sections
- [ ] `preflight-lite.ps1` runs 3 checks in ≤5 seconds
- [ ] `twig-sdlc-planning.yaml` and `twig-sdlc-implement.yaml` have `preflight_lite` as entry point
- [ ] Human gate fires with clear remediation instructions on any required failure
- [ ] Full preflight adds no more than 10 seconds to workflow startup
- [ ] Pester tests cover all checks with mocked external commands
- [ ] `conductor validate` passes on all 3 modified workflow YAMLs
- [ ] SKILL.md documents the preflight phase and remediation steps

## PR Groups

| PG | Name | Type | Tasks | Est. LoC | Est. Files | Predecessor |
|----|------|------|-------|----------|------------|-------------|
| PG-1 | Preflight enhancements + sub-workflow wiring | Deep | T1, T2, T3, T4 | ~290 | 5 | — |
| PG-2 | Tests + documentation | Wide | T5, T6 | ~240 | 3 | PG-1 |

**PG-1** (Deep): Core script and YAML changes — enhances the existing preflight script, creates the lite variant, and wires both into all 3 workflow YAMLs. All changes are in `twig-conductor-workflows`.

**PG-2** (Wide): Pester tests for both scripts and SKILL.md documentation update. Tests are in `twig-conductor-workflows`; SKILL.md is in `twig`. Depends on PG-1 because tests validate the enhanced script output schema.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | `PG-1-preflight-enhancements-wiring` | #2071 / T1, T2, T3, T4 | — | Deep |
| PG-2 | `PG-2-tests-documentation` | #2071 / T5, T6 | PG-1 | Wide |

### Execution Order

**PG-1 first** — all core script and YAML changes must land before tests can be written against the new output schema. PG-1 is entirely in `twig-conductor-workflows` (5 files, ~290 LoC) and is independently buildable: `conductor validate` can verify YAML correctness, and the enhanced `preflight-check.ps1` plus new `preflight-lite.ps1` can be smoke-tested manually without the Pester suite.

**PG-2 second** — Pester tests depend on the actual scripts from PG-1 to validate output schemas and check semantics. The `SKILL.md` documentation update is placed here because it documents the completed system; it can only be written accurately once PG-1 shapes are finalized. PG-2 spans two repos (`twig-conductor-workflows` for tests, `twig` for SKILL.md) — each file is self-contained with no cross-repo build dependency.

### Validation Strategy

**PG-1 validation:**
- `conductor validate` on `twig-sdlc-full.yaml`, `twig-sdlc-planning.yaml`, `twig-sdlc-implement.yaml` — verifies YAML syntax and routing reachability
- Manual smoke test: run `preflight-check.ps1` locally, verify JSON output has `required_checks`, `advisory_checks`, `has_warnings`, and `warning_count` fields
- Manual smoke test: run `preflight-lite.ps1` locally, verify output schema matches full preflight structure
- Verify `Measure-Command { ./preflight-check.ps1 }` ≤ 10s on healthy system
- Verify `Measure-Command { ./preflight-lite.ps1 }` ≤ 5s on healthy system

**PG-2 validation:**
- `Invoke-Pester tests/preflight-check.Tests.ps1` — all 10+ test cases pass
- `Invoke-Pester tests/preflight-lite.Tests.ps1` — all output schema tests pass
- SKILL.md renders correctly in GitHub Markdown preview
- Re-run `conductor validate` to confirm no YAML regressions from PG-1

## References

- [Conductor Authoring Guide](../../.github/skills/conductor/references/authoring.md) — Script agent schema, routing patterns
- [Conductor Design Principles](../../.github/skills/conductor-design/SKILL.md) — P8 (prefer scripts), P3 (re-entry), P10 (invariants)
- [SDLC Redesign Journal](sdlc-redesign-journal.md) — Pipeline architecture, script agent output model
- [twig-sdlc SKILL.md](../../.github/skills/twig-sdlc/SKILL.md) — Current SDLC launch instructions
