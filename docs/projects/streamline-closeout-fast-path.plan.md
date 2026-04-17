# Streamline Close-Out Agent Fast-Path

> **Status**: 🔨 In Progress
> **Work Item**: [#1744](https://dev.azure.com/) — Streamline close-out agent when top-level item is already Done
> **Parent Issue**: #1741 — Query Command (Epic 1302) Closeout Findings — Closeout Notes
> **Parent Epic**: #1603 — Follow Up on Closeout Findings
> **Revision**: 2 (post-review)

---

## Executive Summary

When the twig-sdlc close-out agent runs against an Epic/Issue that is already in "Done" state, it currently performs the full upstream verification suite — syncing the cache, verifying all PRs are merged, checking for orphaned branches, verifying child-item states — before reaching its idempotency check at Step 2. This is wasted effort. This plan adds an early fast-path check immediately after the cache sync (Step 0) that detects the "already Done" state and skips Steps 1–4 (PR verification, branch checks, child-item checks, state transition, plan update), jumping directly to the observations/meta-analysis phase (Steps 5–10). The change is purely to prompt engineering files — no C# code changes are required.

## Background

### Current Architecture

The close-out agent (`close_out`) is Phase 5 of the twig-sdlc Conductor workflow. It runs after the `pr_finalizer` agent verifies all PR groups have merged PRs. The agent is defined in:

| File | Role |
|------|------|
| `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | Agent prompt with step-by-step instructions |
| `.github/skills/twig-sdlc/assets/prompts/close-out.system.md` | System prompt defining agent identity |
| `.github/skills/twig-sdlc/assets/twig-sdlc.yaml` (lines 800–829) | Agent definition: inputs, outputs, model, routing |

### Current Step Flow

The close-out agent has 11 steps (0 through 10):

| Step | Name | Category | Time Cost |
|------|------|----------|-----------|
| 0 | Sync local cache | Preparation | Low-moderate |
| 1 | Verify all PRs merged | Verification | High (N × `gh pr view`) |
| 1b | Verify no unmerged branches | Verification | Moderate |
| 1c | Verify all child items Done | Verification | Moderate (`twig tree`) |
| 2 | Idempotency check | Decision point | Low |
| 3 | Transition Epic to Done | Mutation | Low |
| 4 | Update plan document | Mutation | Low |
| 5 | Full workflow git log | Observation | Low |
| 6 | Produce meta-observations | Analysis | LLM reasoning time |
| 7 | Record observations in ADO | Recording | Low |
| 8 | Push commits | Finalization | Low |
| 9 | Final commit if needed | Finalization | Low |
| 10 | Tag release | Finalization | Low |

### The Existing Idempotency Check (Step 2)

Step 2 currently reads:
> `twig set <epic_id> --output json` — read the current state
> `git log --oneline -10` — check if a close commit already exists
> If state is already "Done" AND a close commit exists, skip steps 3-4 and go to step 5

This is a *partial* optimization — it skips the mutation steps (3-4) but still performs all verification steps (1, 1b, 1c) that precede it. When the item is already Done, those verification steps are redundant: the verification was already performed during the original close-out run that transitioned the item to Done.

### Routing Constraint

The `close_out` agent is only reachable when `pr_finalizer.output.verified` is `true` (YAML line 771: `when: "{{ output.verified }}"`). This means that by the time `close_out` executes, the `pr_finalizer` has already verified that all PR groups have merged PRs. This routing constraint provides an additional safety guarantee for the fast-path: skipping Step 1 (PR verification) is safe not only because the item is already Done, but because the upstream gate has already confirmed PR merge state.

### Impact Summary

The fast-path change has zero downstream impact. Steps 5–10 still execute, producing all output fields consumed by `closeout_filer` (`observations`, `improvements`, `agent_struggles`). The only output that differs is `epic_completed`, which is `false` on the fast-path (the agent did not perform the transition).

## Problem Statement

When the close-out agent is invoked for an Epic/Issue already in "Done" state (e.g., during a retry, manual re-run, or when the workflow was interrupted and restarted), Steps 1 through 1c perform expensive verification that is both unnecessary and potentially fragile:

1. **Wasted API calls**: Step 1 runs `gh pr view` for every PR in the completed list. Step 1c runs `twig tree --output json` and inspects every child.
2. **False-positive risk**: Verification steps may flag issues that were already resolved during the original close-out, leading to unnecessary STOP conditions.
3. **Token waste**: On an Opus 1M model, the verification steps consume significant reasoning tokens that add no value when the outcome is predetermined.

## Goals and Non-Goals

### Goals

1. **Reduce wasted computation**: When the top-level Epic/Issue is already Done, skip the expensive verification suite (Steps 1–4) that re-checks already-verified state
2. **Preserve observability outputs**: The fast-path must still produce all output fields consumed by downstream agents (`observations`, `improvements`, `agent_struggles`, `epic_completed`)
3. **Self-documenting prompt**: The fast-path logic and its rationale must be clearly explained in the prompt for future maintainers

### Non-Goals

- **Changing `closeout_filer`**: The downstream filing agent is not modified; it consumes the same output fields regardless of path taken
- **YAML-level routing changes**: The fast-path is implemented purely in prompt logic, not as a workflow routing gate (see Alternatives Considered)
- **Modifying the normal (non-Done) flow**: Steps 1–4 remain unchanged for the standard close-out path
- **Applying fast-path to `closeout_filer`**: The filer agent runs unconditionally after close-out; optimizing it is out of scope

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR1 | When `twig set <epic_id> --output json` shows state "Done" after Step 0 sync, skip Steps 1–4 |
| FR2 | The fast-path must still execute Steps 5–10 (git log, observations, ADO notes, push, commit, tag) |
| FR3 | The fast-path must set `epic_completed: false` in the output (agent did not transition) |
| FR4 | The fast-path decision point must include a clear log/note explaining why verification was skipped |
| FR5 | The existing Step 2 idempotency check remains unchanged for the normal (non-fast-path) flow |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR1 | The prompt change must be backward-compatible — no YAML schema changes |
| NFR2 | The fast-path documentation in the prompt must be self-explanatory for future maintainers |

## Proposed Design

### Architecture Overview

The change is confined entirely to the close-out agent's prompt layer — no YAML workflow changes, no C# code changes, no new agents. A new Step 0b is inserted between Step 0 (sync) and Step 1 (PR verification): if the Epic state is already "Done", skip to Step 5; otherwise continue normally.

### Key Design Decisions

#### D1: Fast-path check placement — after Step 0, before Step 1

**Decision**: Place the fast-path check after the cache sync (Step 0) but before any verification steps (Step 1).

**Rationale**: Step 0 (`twig sync`) ensures the local DB has fresh state from ADO. Without sync, the local state might show "Doing" even though ADO was already transitioned to "Done" by a previous run. The sync is lightweight (single API call) and is essential for accurate state detection.

**Alternative rejected**: Placing the check before Step 0 (skip sync entirely). This risks stale local state leading to a missed fast-path opportunity, defeating the optimization.

**Additional safety note**: The `close_out` agent is only reachable when `pr_finalizer.output.verified` is `true` (YAML line 771). This upstream routing constraint means basic PR merge state has already been validated before `close_out` runs, making the fast-path's skip of Step 1 (PR verification) even safer — it is not the only guard.

#### D2: State-only check vs. state + close-commit check

**Decision**: Check only the Epic state (is it "Done?"). Do NOT require a close commit to exist.

**Rationale**: The existing Step 2 idempotency check requires BOTH "Done" state AND a close commit. The fast-path is broader: if the item is Done — regardless of how it got there (manual transition, previous close-out run, API call) — the verification steps are unnecessary. The observations phase should still run to produce meta-analysis even if no close commit exists yet.

### Detailed Prompt Changes

#### 1. New Fast-Path Section (inserted between Step 0 and Step 1)

A new clearly-demarcated section titled "**Fast-Path: Already-Done Check**" will be inserted after Step 0. It will:

1. Run `twig set {{ intake.output.epic_id }} --output json` to read the current state
2. If the state is "Done":
   - Log a note explaining the fast-path: "Epic is already Done — skipping PR/branch/child verification (Steps 1–4) and proceeding to observations"
   - Set `epic_completed: false` (the agent did not perform the transition)
   - Jump to Step 5
3. If the state is NOT "Done": continue with Step 1 (normal flow)

The section will include a documentation block explaining:
- Why the verification steps are safe to skip (they were already performed during the original transition)
- When this fast-path triggers (re-runs, retries, manual transitions)
- What is NOT skipped (observations, ADO notes, push, tag)

#### 2. Step Renumbering

Current steps will be renumbered to accommodate the new fast-path check logically. The fast-path will be labeled as Step 0b (to maintain the existing numbering scheme that uses sub-steps like 1b, 1c):

- Step 0: Sync local cache (unchanged)
- **Step 0b: Fast-path already-Done check (NEW)**
- Step 1: Verify PRs merged (unchanged, only reached if not Done)
- Step 1b: Verify no unmerged branches (unchanged)
- Step 1c: Verify all child items Done (unchanged)
- Step 2: Idempotency check (unchanged)
- Steps 3–10: unchanged

#### 3. Output Field Handling

On the fast-path, `epic_completed` must be set to `false` because the agent did not perform the state transition. The observations fields (`observations`, `agent_struggles`, `improvements`) are produced by Steps 5-6, which always execute regardless of path.

## Alternatives Considered

| # | Alternative | Pros | Cons | Decision |
|---|-------------|------|------|----------|
| A1 | **YAML-level routing gate** — Add a conditional route before `close_out` that checks state and routes to a lightweight `close_out_fast` agent | More deterministic (no LLM instruction-following risk); clean separation of agents | Requires a new agent definition and YAML schema changes; duplicates Steps 5–10 across two agents; violates NFR1 (backward-compatible, no YAML changes); adds maintenance burden of keeping two agents in sync | **Rejected** — the added complexity outweighs the marginal determinism gain for a low-frequency scenario |
| A2 | **Expand existing Step 2 idempotency check** — Move the current Step 2 check earlier (to after Step 0) instead of adding a new Step 0b | Minimal prompt change; reuses existing logic | Semantics differ: Step 2 checks Done AND close-commit existence, while the fast-path checks Done only. Muddying the two checks into one reduces clarity and makes future modifications harder. Step 2 would need to handle two different "Done" sub-cases in one block | **Rejected** — cleaner to add a distinct fast-path check with its own documentation than to overload Step 2 |
| A3 | **State-only check** (adopted) vs **state + close-commit check** | Catches all Done states regardless of origin (manual, previous run, API); broader coverage | May fast-path even when no close commit exists yet | **Adopted** — the observations phase should still run regardless of how Done was reached; close-commit existence is irrelevant to whether verification should be skipped |

Design decisions D1 (placement after Step 0) and D2 (state-only check) are documented in the Proposed Design section with full rationale.

## Risks and Mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R1 | **LLM compliance**: The model ignores the fast-path conditional and runs verification anyway, or skips too many steps (past Step 5) | Low | Low | The fast-path uses clear conditional structure ("If state is already Done … SKIP directly to Step 5") with explicit step enumeration. If the model runs verification anyway, the result is correct but slower — no harm. If it skips past Step 5, the output fields will be empty, which `closeout_filer` would surface as an obviously broken filing. The close-out system prompt can be updated to reinforce the fast-path if compliance issues are observed. |
| R2 | **Manual-Done edge case**: Someone manually sets an Epic to Done before the workflow completes, causing the fast-path to skip verification of potentially unmerged PRs or open child items | Low | Medium | Acceptable by design. If the item was manually set to Done, the user has intentionally declared completion. The observations phase (Steps 5–10) still runs and will note any anomalies in its meta-analysis. Additionally, the `pr_finalizer` upstream gate (YAML line 771) already verified PR merge state before `close_out` runs — so basic PR merge state is guaranteed even in this edge case. |
| R3 | **Observability**: When the fast-path triggers, the user may not realize verification was skipped | Low | Low | FR4 requires a `twig note` explaining the fast-path decision. This note appears in the ADO work item's Discussion tab as an auditable comment. The note text explicitly states: "Fast-path: Epic/Issue is already Done — skipping PR/branch/child verification (Steps 1–4) and proceeding directly to observations." Users reviewing the ADO item will see this note alongside any other workflow comments. |

## Testing and Validation

Since this is a prompt-only change, validation is performed by running the workflow against an already-Done Epic and confirming:

- ✅ Agent calls `twig sync` (Step 0) then detects "Done" state via `twig set`
- ✅ Agent logs a `twig note` explaining the fast-path (FR4)
- ✅ Agent skips Steps 1–4 — no `gh pr view`, no `twig tree`, no `twig state Done`
- ✅ Agent proceeds to Step 5 and produces non-empty `observations`, `improvements`, `agent_struggles`
- ✅ Agent output includes `epic_completed: false`
- ✅ Downstream `closeout_filer` successfully creates an ADO Issue from the observations

**Regression:** Run against a "Doing" Epic to confirm Steps 1–4 still execute normally.

## Open Questions

*None — all design decisions are settled.*

## Files Affected

### New Files

*None*

### Modified Files

| File Path | Changes |
|-----------|---------|
| `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | Add fast-path check (Step 0b) after sync, with documentation block and skip-to-Step-5 logic |

## ADO Work Item Structure

This plan implements a single Task within an existing ADO hierarchy:

- **Epic #1603**: Follow Up on Closeout Findings
  - **Issue #1741**: Query Command (Epic 1302) Closeout Findings — Closeout Notes
    - **Task #1744**: Streamline close-out agent when top-level item is already Done *(this task)*

Since the input work item is already a leaf-level Task, no further decomposition into child work items is needed. The implementation scope is described in the work breakdown below.

### Work Breakdown for Task #1744

| Step | Description | Files | Est. LoC |
|------|-------------|-------|----------|
| 1 | Insert fast-path check block (Step 0b) between Step 0 and Step 1 in `close-out.prompt.md`. The block includes: state check via `twig set`, conditional branch on "Done", `twig note` for observability, `epic_completed: false` assignment, and explicit "SKIP directly to Step 5" instruction. | `close-out.prompt.md` | ~15 lines |
| 2 | Add documentation block within Step 0b explaining why verification is safe to skip (original run already verified), when the fast-path triggers (re-runs, retries, manual transitions), and what is NOT skipped (Steps 5–10). | `close-out.prompt.md` | ~10 lines |

**Acceptance Criteria:**
- [ ] Step 0b appears between Step 0 and Step 1 in the prompt
- [ ] State check uses `twig set {{ intake.output.epic_id }} --output json`
- [ ] Fast-path includes `twig note` with explanatory message
- [ ] Fast-path sets `epic_completed: false`
- [ ] Fast-path instruction explicitly says "SKIP directly to Step 5"
- [ ] Documentation block explains: why safe, when triggers, what's not skipped
- [ ] Existing Steps 1–10 are unchanged
- [ ] Step 2 idempotency check remains intact for normal flow

### Actual Prompt Text for Step 0b

The following text will be inserted into `close-out.prompt.md` between Step 0 and Step 1:

```markdown
0b. **Fast-path: Already-Done check** (skip redundant verification when re-running):
   - `twig set {{ intake.output.epic_id }} --output json` — read the current state
   - If the state is already "Done":
     1. Record the fast-path decision:
        `twig note --text "Fast-path: Epic/Issue is already Done — skipping PR/branch/child verification (Steps 1–4) and proceeding directly to observations."`
     2. Set `epic_completed: false` (the agent did not perform the transition)
     3. **SKIP directly to Step 5** — do NOT execute Steps 1, 1b, 1c, 2, 3, or 4
   - If the state is NOT "Done": continue with Step 1 (normal flow)

   > **Why this is safe:** When an item is already Done, the verification steps (PR merge
   > checks, branch checks, child-item checks) were already performed during the original
   > close-out run that transitioned the item. Re-running them is wasted effort and may
   > produce false-positive STOP conditions. Additionally, the `pr_finalizer` upstream gate
   > (YAML routing: `when: "{{ output.verified }}"`) has already confirmed PR merge state
   > before this agent runs — Step 1 verification is not the only guard.
   >
   > **When this triggers:** Re-runs, retries, manual workflow restarts, or cases where the
   > item was manually transitioned to Done before the workflow completed.
   >
   > **What is NOT skipped:** Steps 5–10 (git log, meta-observations, ADO notes, push,
   > commit, tag) always execute to ensure observations are produced for the closeout_filer.
```

## References

- Current `close-out.prompt.md`: `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md`
- Workflow YAML (close_out agent definition): `.github/skills/twig-sdlc/assets/twig-sdlc.yaml` lines 800–829
- Close-out system prompt: `.github/skills/twig-sdlc/assets/prompts/close-out.system.md`
- Closeout filer prompt: `.github/skills/twig-sdlc/assets/prompts/closeout-filer.prompt.md`
- PR grouping instructions: `.github/instructions/pr-grouping.instructions.md`
- PR finalizer routing gate: `.github/skills/twig-sdlc/assets/twig-sdlc.yaml` line 771
