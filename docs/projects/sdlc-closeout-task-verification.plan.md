---
work_item_id: 2070
type: Issue
title: "SDLC close_out: drill into Issues to verify Task states before Epic closure"
status: Draft
revision: 0
---

# SDLC close_out: drill into Issues to verify Task states before Epic closure

| Field | Value |
|-------|-------|
| **Work Item** | #2070 |
| **Type** | Issue |
| **Status** | Draft |

## Executive Summary

The SDLC close_out agent currently verifies that all direct children (Issues) of
an Epic are in state "Done" before transitioning the Epic, but it never drills into
those Issues to check whether their child Tasks are also Done. This creates a gap
where an Issue could be marked Done while one or more of its Tasks remain in "To Do"
or "Doing" state — allowing the Epic to close with incomplete work. This plan
enhances the close_out prompt to explicitly iterate each Issue's children via
`twig_children`, verify all Tasks are in a terminal state, and roll back orphaned
Tasks when the workflow ends with incomplete work.

## Background

### Current Architecture

The SDLC lifecycle is orchestrated by `twig-sdlc-full.yaml`:

```
state_detector (script) → planning → implementation → close_out (agent) → retrospective
```

The **close_out agent** (`claude-opus-4.6-1m`) is defined in the apex workflow and
receives the work item ID plus implementation output. Its behavior is governed by
two prompt files:

- **`close-out.system.md`** — role definition, verification rules, postconditions
- **`close-out.prompt.md`** — step-by-step instructions (Steps 0–10)

### How close_out currently verifies child states (Step 1c)

```
Step 1c: Verify all child items are Done
  - twig set <id> --output json
  - twig tree --output json — inspect all children
  - If ANY child Issue or Task is NOT in state "Done": ...
```

The prompt mentions "Issue or Task" but **`twig tree` only returns direct children**
of the focused item. For an Epic, this means:
- ✅ Issues are visible (direct children of Epic)
- ❌ Tasks are **NOT visible** (children of Issues, not of Epic)

### Tool behavior analysis

| Tool | What it returns | Depth |
|------|----------------|-------|
| `twig_tree` | Focus item + direct children + parent chain | 1 level of children |
| `twig_children <id>` | Direct children of the given work item | 1 level |
| `twig_batch` | Parallel/sequential execution of multiple tool calls | N/A |

The `twig tree` MCP tool calls `GetChildrenAsync(item.Id)` which returns only direct
children. The `--depth` parameter controls the **max count** of children displayed,
not recursion depth. The JSON output via `WriteWorkItemArray` emits a flat array of
work items with no nested `children` property.

To see Tasks under Issues, the agent must explicitly call `twig_children` for each
Issue ID.

### Related patterns in the codebase

1. **`load-work-tree.ps1`** (implementation phase) — already handles Epic → Issue →
   Task hierarchy by calling `twig tree --depth 2` and iterating `$child.children`.
   However, this script accesses `$child.children` which is NOT populated in the MCP
   JSON output shape — the script relies on CLI JSON formatter behavior.

2. **`pr-finalizer.prompt.md`** Step 5b — has "Verify Task states match reality
   (defense-in-depth)" but only checks Tasks within completed PR groups, not all
   Tasks across the entire hierarchy.

3. **`detect-state.ps1`** — counts `doneCount`, `doingCount`, `todoCount` for direct
   children only. This affects routing accuracy but not close_out behavior directly.

### Call-site audit

The close_out prompt's verification steps are consumed in one place:

| File | Location | Current Usage | Impact |
|------|----------|---------------|--------|
| `close-out.prompt.md` | Step 1c | `twig tree` for child state check | Primary change target |
| `close-out.prompt.md` | Step 1e | `twig tree` output for rollback | Secondary change target |
| `close-out.system.md` | Postconditions | "If all children Done" | Needs clarification |
| `twig-sdlc-full.yaml` | close_out agent definition | Routes to retrospective | No change needed |
| `detect-state.ps1` | Step 2 | Direct children only | Out of scope (routing, not closure) |

## Problem Statement

The close_out agent can transition an Epic to "Done" when Issues show as "Done" but
their child Tasks are still in non-terminal states. Specifically:

1. **No Task visibility**: `twig tree` from the Epic level shows Issues but not Tasks.
   The prompt says "If ANY child Issue or Task is NOT in state Done" but the tool
   output physically cannot surface Task states.

2. **Incomplete rollback**: Step 1e rolls back orphaned "Doing" Issues but does not
   address orphaned "Doing" Tasks under those Issues. Tasks stuck in "Doing" after a
   partial workflow run create misleading state on the ADO board.

3. **Postcondition gap**: The system prompt's postcondition says "If all children Done"
   without explicitly requiring Task-level verification, making the contract ambiguous.

## Goals and Non-Goals

### Goals

1. Close_out agent verifies that ALL Tasks under ALL Issues are in "Done" state before
   transitioning an Epic to Done
2. Close_out agent reports specific non-Done Tasks (IDs, titles, current states,
   parent Issue) when verification fails
3. Orphaned "Doing" Tasks are rolled back to "To Do" when the workflow ends with
   incomplete work
4. System prompt postconditions explicitly require Task-level verification

### Non-Goals

1. **Enhancing `detect-state.ps1`** — that script handles routing, not closure. While
   it has a similar gap, fixing it is a separate concern.
2. **Adding recursive depth to `twig_tree`** — the MCP tool is designed for focused
   single-level views. Using `twig_children` per Issue is the intended pattern.
3. **Modifying `twig-sdlc-full.yaml`** — the workflow structure is correct; only
   the agent's instructions need enhancement.
4. **Adding automated tests for prompt behavior** — prompt testing is out of scope
   for this change; manual validation via workflow runs is sufficient.

## Requirements

### Functional

- **FR-1**: Step 1c MUST call `twig_children` for each child Issue to retrieve Tasks
- **FR-2**: Step 1c MUST check that every Task is in state "Done" before setting
  `epic_completed: true`
- **FR-3**: If any Task is NOT Done, close_out MUST report the Task IDs, titles,
  current states, and parent Issue ID/title
- **FR-4**: Step 1e MUST identify and roll back "Doing" Tasks to "To Do" for
  orphaned or incomplete workflow runs
- **FR-5**: The `twig_batch` tool SHOULD be used for parallel `twig_children` calls
  to minimize latency

### Non-Functional

- **NFR-1**: Prompt changes must not increase token usage significantly (batch
  parallel calls, avoid redundant tree reads)
- **NFR-2**: Changes must be backward-compatible with existing close_out behavior
  for Issues (which have Tasks as direct children — already visible)
- **NFR-3**: Fast-path (Step 0b) must remain unchanged — already-Done items skip
  verification entirely

## Proposed Design

### Architecture Overview

No architectural changes. The close_out agent remains a single LLM agent in the
apex workflow. The change is purely to the prompt instructions that govern its
tool-calling behavior.

```
close_out agent
  ├─ Step 0: Read state (unchanged)
  ├─ Step 0b: Fast-path (unchanged)
  ├─ Step 1: Verify PRs merged (unchanged)
  ├─ Step 1b: Verify no unmerged branches (unchanged)
  ├─ Step 1c: Verify child states ← ENHANCED (drill into Issues → Tasks)
  ├─ Step 1e: Roll back orphans ← ENHANCED (include Tasks)
  ├─ Steps 2-10: (unchanged)
```

### Key Components

#### Enhanced Step 1c: Deep hierarchy verification

**Current behavior:**
```
twig tree --output json → check children array → all Done?
```

**New behavior:**
```
twig tree --output json → for each Issue in children:
  twig_children <issue_id> → check each Task is Done
→ all Issues Done AND all Tasks Done?
```

The agent will:
1. Read the tree as before to get Issue-level children
2. Collect all Issue IDs from the children array
3. Use `twig_batch` with parallel `twig_children` calls for all Issues
4. Inspect each Task's state in the returned results
5. Build a consolidated list of non-Done items (Issues AND Tasks)
6. Set `epic_completed` based on ALL levels being Done

#### Enhanced Step 1e: Task-level rollback

**Current behavior:**
```
For each orphaned "Doing" Issue → twig state "To Do" --force
```

**New behavior:**
```
For each Issue (including Done Issues with orphaned Tasks):
  twig_children <issue_id> → for each "Doing" Task:
    twig set <task_id> → twig state "To Do" --force
For each orphaned "Doing" Issue → twig state "To Do" --force (unchanged)
```

Task rollback happens BEFORE Issue rollback to prevent state inconsistencies
(rolling back an Issue while its Tasks are still in various states).

### Data Flow

```
Epic (focused)
  │
  ├─ twig tree → children: [Issue-1, Issue-2, Issue-3]
  │
  ├─ twig_batch (parallel):
  │   ├─ twig_children Issue-1.id → [Task-1a, Task-1b]
  │   ├─ twig_children Issue-2.id → [Task-2a, Task-2b, Task-2c]
  │   └─ twig_children Issue-3.id → [Task-3a]
  │
  ├─ Verify:
  │   ├─ All Issues Done? ✅
  │   ├─ All Tasks Done? ❌ (Task-2c is "Doing")
  │   └─ epic_completed = false
  │
  └─ Step 1e: Roll back Task-2c → "To Do"
```

### Design Decisions

1. **Prompt-only change (no script or code changes)**: The close_out agent is already
   an LLM agent (claude-opus-4.6-1m) that makes tool calls. Adding explicit
   `twig_children` instructions fits the existing pattern. A deterministic script
   (P8) was considered but rejected because:
   - The close_out agent already performs non-deterministic verification (PR checks,
     branch checks, git log analysis)
   - Adding a script node would require modifying the workflow YAML
   - The existing prompt pattern of explicit tool commands provides sufficient
     determinism for this verification

2. **Use `twig_batch` for parallel children fetches**: Rather than sequential
   `twig_children` calls (one per Issue), the prompt instructs the agent to use
   `twig_batch` with parallel execution. This minimizes latency when there are
   multiple Issues.

3. **Task rollback before Issue rollback**: Rolling back Tasks first ensures that
   when an Issue is rolled back, its children are in a consistent state. This
   prevents the scenario where an Issue is "To Do" but its Tasks are "Doing".

## Dependencies

- **`twig_children` MCP tool**: Already exists in `NavigationTools.cs`. Returns
  direct children of any work item by ID. No changes needed.
- **`twig_batch` MCP tool**: Already exists in `BatchTools.cs`. Supports parallel
  execution of multiple tool calls. No changes needed.
- **Conductor registry**: Changes target `~/.conductor/registries/twig/prompts/`.
  These files are synced from `PolyphonyRequiem/twig-conductor-workflows`.

## Open Questions

1. **[Low] Should `detect-state.ps1` also drill into Tasks?** The state detector
   currently only counts direct children for routing decisions. If an Epic's Issues
   are all Done but Tasks aren't, the detector might route to `done` prematurely.
   This is a separate concern and could be tracked as a follow-up issue.

2. **[Low] Should the verification handle more than 2 levels?** The current ADO
   hierarchy is Epic → Issue → Task (3 levels). If custom process templates allow
   deeper hierarchies, the verification would need to recurse further. For now,
   the Basic process template's 3-level hierarchy is sufficient.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `~/.conductor/registries/twig/prompts/close-out.prompt.md` | Enhance Step 1c with Issue→Task drilldown; enhance Step 1e with Task rollback |
| `~/.conductor/registries/twig/prompts/close-out.system.md` | Update postconditions to explicitly require Task-level verification |

## ADO Work Item Structure

This is an Issue (#2070). Tasks are defined directly under it.

### Issue #2070: SDLC close_out: drill into Issues to verify Task states before Epic closure

**Goal**: Ensure the close_out agent verifies Task states under each Issue before
allowing Epic closure, and rolls back orphaned Tasks on incomplete runs.

**Prerequisites**: None — this is a standalone prompt enhancement.

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T1 | Enhance Step 1c: add `twig_children` drilldown for each Issue to verify all Tasks are Done; use `twig_batch` for parallel fetches; add detailed non-Done reporting | `close-out.prompt.md` | ~60 LoC prompt |
| T2 | Enhance Step 1e: add Task-level rollback for orphaned "Doing" Tasks before Issue rollback | `close-out.prompt.md` | ~30 LoC prompt |
| T3 | Update system prompt postconditions to explicitly require Task-level verification | `close-out.system.md` | ~10 LoC prompt |
| T4 | Validate changes: run close_out against a test Epic with mixed Task states and verify correct blocking behavior | Manual verification | ~30 min |

#### Acceptance Criteria

- [ ] Step 1c calls `twig_children` for each Issue under the Epic
- [ ] Step 1c blocks Epic closure when any Task is not Done
- [ ] Step 1c reports specific non-Done Tasks with IDs, titles, states, and parent Issue
- [ ] Step 1e rolls back "Doing" Tasks to "To Do" on incomplete runs
- [ ] System prompt postconditions mention Task-level verification
- [ ] Fast-path (Step 0b) remains unchanged — already-Done items skip verification
- [ ] Issue-level close_out behavior (no children hierarchy) works unchanged

## PR Groups

### PG-1: Close-out prompt enhancement (deep)

**Classification**: Deep — 2 files, complex prompt logic changes

**Contains**:
- T1: Enhance Step 1c (close-out.prompt.md)
- T2: Enhance Step 1e (close-out.prompt.md)
- T3: Update postconditions (close-out.system.md)

**Estimated scope**: ~100 LoC of prompt changes across 2 files, ≤2 files

**Successors**: None — single PR group

**Validation**: T4 (manual validation via workflow run)

## References

- [close-out.prompt.md](~/.conductor/registries/twig/prompts/close-out.prompt.md) — primary change target
- [close-out.system.md](~/.conductor/registries/twig/prompts/close-out.system.md) — postcondition update
- [load-work-tree.ps1](~/.conductor/registries/twig/recursive/scripts/load-work-tree.ps1) — reference pattern for Issue→Task iteration
- [NavigationTools.cs](src/Twig.Mcp/Tools/NavigationTools.cs) — `twig_children` implementation
- [ReadTools.cs](src/Twig.Mcp/Tools/ReadTools.cs) — `twig_tree` implementation showing single-level children
- [McpResultBuilder.cs](src/Twig.Mcp/Services/McpResultBuilder.cs) — JSON output shape (no nested children)

