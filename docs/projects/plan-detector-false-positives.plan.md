---
work_item_id: 1918
title: "Plan detector false positives: matches parent epic plans as child issue plans"
type: Issue
status: Draft
revision: 0
revision_notes: "Initial draft."
---

# Plan Detector False Positives: Matches Parent Epic Plans as Child Issue Plans

| Field | Value |
|-------|-------|
| **Work Item** | #1918 |
| **Type** | Issue |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

The plan detection logic in the twig SDLC conductor workflow (`plan_detector` agent in
`twig-sdlc-full.yaml`) matches plan files by grepping for the requested work item ID in
file content. This produces false positives when a child Issue's ID appears in the parent
Epic's plan — the parent plan lists child Items, so the detector incorrectly claims the
parent plan IS the child's approved plan, causing the SDLC to skip planning and proceed
with a mismatched plan. The fix adds YAML frontmatter (`work_item_id`, `title`, `type`)
to all 16 existing `.plan.md` files so the detector can match plans by explicit ID rather
than content heuristics. Two supporting changes update `tools/seed-from-plan.ps1` to read
the new frontmatter fields and update `.github/skills/twig-sdlc/SKILL.md` to document
frontmatter requirements for the architect agent and plan detection behavior.

## Background

### Current Architecture

Plan files live in `docs/projects/*.plan.md` and are created by the SDLC architect agent
during the Planning phase. The `plan_reader` agent (Phase 2, Sonnet model) reads existing
approved plans to bypass the full planning flow when a plan already exists.

Plan detection currently works by **content matching**: the `plan_detector` agent searches
all `docs/projects/*.plan.md` files for references to the requested work item ID. This is
a simple `grep` for the numeric ID pattern across all plan file content.

### Plan File Metadata Formats

The 16 existing plan files use **four different ad-hoc formats** for metadata, none of
which include machine-parseable YAML frontmatter:

**Format 1: Markdown table** (7 files)
```markdown
| Field | Value |
|-------|-------|
| **Work Item** | #1890 |
| **Type** | Issue |
```
Used by: `auto-tag-twig-created-items`, `az-cli-timeout-override`, `azcli-auth-timeout`,
`expand-mcp-tools`, `guard-duplicate-work-item-creation`, `mcp-read-write-tools-expansion`,
`parent-state-propagation`, `read-only-lookup-tools`

**Format 2: Blockquote** (4 files)
```markdown
> **Work Item**: [#1782](https://dev.azure.com/...)
> **Type**: Issue
```
Used by: `batch-state-field-updates`, `mcp-error-handling-html-guard`,
`mcp-multi-workspace`, `streamline-closeout-fast-path`

**Format 3: Bold inline** (3 files)
```markdown
**Work Item:** #1643 — Title
**Type:** Issue
```
Used by: `child-state-verification-gate`, `companion-auto-upgrade`,
`sync-perf-optimization-v3`

**Format 4: Mixed/custom** (2 files)
```markdown
**Epic:** #1263 — Twig VSCode Extension
```
Used by: `twig-vscode-extension`

### seed-from-plan.ps1

The `tools/seed-from-plan.ps1` script creates ADO work items from plan documents. Its
`Get-PlanTitle` function currently resolves the plan title by:
1. Trying YAML frontmatter `goal:` field first
2. Falling back to the first H1 heading
3. Falling back to the filename

It has **no function** to extract a work item ID from the plan file. The plan→ADO mapping
is maintained in `tools/plan-ado-map.json` as a separate side-car file.

### Call-Site Audit: `Get-PlanTitle`

| File | Function | Current Usage | Impact |
|------|----------|--------------|--------|
| `tools/seed-from-plan.ps1` | `Get-PlanTitle` | Parses `goal:` frontmatter or H1 for Epic title | Must also look for `title:` frontmatter field |
| `tools/seed-from-plan.ps1` | Main (line 233) | Calls `Get-PlanTitle` to resolve `$PlanTitle` | No change — function signature unchanged |

### Conductor Workflow (external)

The conductor workflows live in an external repo (`PolyphonyRequiem/twig-conductor-workflows`).
The `plan_detector` agent is defined in `twig-sdlc-full.yaml`. This plan does **not** modify
the conductor workflows — it creates the data contract (frontmatter) that the detector can
consume. The workflow changes to read frontmatter will be handled separately in the external
repo after the frontmatter is established here.

## Problem Statement

1. **False positive plan detection**: When running `twig-sdlc-full@twig --input work_item_id=1852`,
   the plan detector finds `mcp-read-write-tools-expansion.plan.md` (for Epic #1814) because
   it lists child Issue #1852 in its ADO hierarchy section. The workflow treats the parent
   Epic's plan as the child Issue's plan, skipping planning entirely with a scope mismatch.

2. **No machine-readable plan identity**: Plan files embed their target work item ID in
   human-readable markdown (tables, blockquotes, inline bold) with no standardized,
   parseable format. This forces plan detection to rely on content search heuristics.

3. **seed-from-plan.ps1 has no work item ID extraction**: The script cannot programmatically
   read which work item a plan targets, limiting automation options.

## Goals and Non-Goals

### Goals

1. Every `.plan.md` file in `docs/projects/` has YAML frontmatter with `work_item_id`,
   `title`, and `type` fields
2. `tools/seed-from-plan.ps1` can extract the work item ID from frontmatter via a new
   `Get-PlanWorkItemId` function
3. `Get-PlanTitle` prefers the frontmatter `title` field over the `goal:` field and H1 heading
4. `.github/skills/twig-sdlc/SKILL.md` documents the frontmatter contract so the architect
   agent emits it in future plans
5. The frontmatter format is defined clearly enough that the external conductor workflow
   can consume it without ambiguity

### Non-Goals

- **Modifying conductor workflows**: The `plan_detector` / `plan_reader` agent changes
  are in the external `twig-conductor-workflows` repo — out of scope for this plan
- **Removing existing metadata**: The human-readable metadata tables/blockquotes in plan
  files are preserved; frontmatter is additive
- **Validating frontmatter at CI time**: No linting or CI enforcement is added in this plan
- **Changing the plan file naming convention**: File slugs remain as-is

## Requirements

### Functional Requirements

1. **FR-1**: All 16 `.plan.md` files must have YAML frontmatter with three required fields:
   - `work_item_id` (integer) — the ADO work item ID this plan targets
   - `title` (string) — the plan's title/goal
   - `type` (string) — the ADO work item type (`Epic`, `Issue`, or `Task`)

2. **FR-2**: `Get-PlanWorkItemId` function in `seed-from-plan.ps1` must:
   - Parse YAML frontmatter for `work_item_id` field
   - Return the integer ID or `$null` if not present

3. **FR-3**: `Get-PlanTitle` function must prefer frontmatter `title:` over `goal:` over H1

4. **FR-4**: SKILL.md must document the frontmatter schema so the architect agent includes
   it in all future plans

### Non-Functional Requirements

1. **NF-1**: Frontmatter parsing must handle missing frontmatter gracefully (backward compat)
2. **NF-2**: No breaking changes to seed-from-plan.ps1 behavior for files without frontmatter

## Proposed Design

### Architecture Overview

The change is purely additive data + scripting — no C# code changes. Three components
are modified:

```
┌─────────────────────────────┐
│ docs/projects/*.plan.md     │ ← Add YAML frontmatter (16 files)
│   ---                       │
│   work_item_id: 1858        │
│   title: "..."              │
│   type: Issue               │
│   ---                       │
└────────────┬────────────────┘
             │ read by
             ▼
┌─────────────────────────────┐
│ tools/seed-from-plan.ps1    │ ← New Get-PlanWorkItemId function
│   Get-PlanTitle (updated)   │   Get-PlanTitle prefers title: field
│   Get-PlanWorkItemId (new)  │
└─────────────────────────────┘

┌─────────────────────────────┐
│ .github/skills/twig-sdlc/  │ ← Document frontmatter contract
│   SKILL.md                  │   for architect agent
└─────────────────────────────┘
```

### Key Components

#### 1. YAML Frontmatter Schema

Every `.plan.md` file gets a YAML frontmatter block as the first content:

```yaml
---
work_item_id: 1858
title: "AzCliAuthProvider Timeout Override"
type: Issue
---
```

**Field definitions:**
- `work_item_id` (required, integer): The ADO work item ID that this plan targets.
  This is the primary key for plan detection.
- `title` (required, string): Human-readable title. Used by `Get-PlanTitle` and
  for display purposes.
- `type` (required, string): ADO work item type — `Epic`, `Issue`, or `Task`.
  Tells the detector the plan's scope level.

The frontmatter is placed before the existing H1 heading. Existing metadata tables
and blockquotes are preserved below the heading — they serve as human-readable
context and are not removed.

#### 2. Get-PlanWorkItemId Function

New PowerShell function in `seed-from-plan.ps1`:

```powershell
function Get-PlanWorkItemId {
    param([string]$Path)
    $lines = Get-Content $Path -Encoding UTF8
    $inFrontmatter = $false
    foreach ($line in $lines) {
        if ($line -match '^---\s*$') {
            if ($inFrontmatter) { break }
            $inFrontmatter = $true
            continue
        }
        if ($inFrontmatter -and $line -match '^\s*work_item_id:\s*(\d+)') {
            return [int]$Matches[1]
        }
    }
    return $null
}
```

This function follows the same frontmatter-parsing pattern already used in
`Get-PlanTitle` for the `goal:` field.

#### 3. Updated Get-PlanTitle

The existing `Get-PlanTitle` function is updated to check for a `title:` frontmatter
field before `goal:` and H1 heading:

```
Priority order:
1. YAML frontmatter `title:` field (new — preferred)
2. YAML frontmatter `goal:` field (existing — backward compat)
3. First H1 heading (existing fallback)
4. Filename without extension (existing last resort)
```

#### 4. SKILL.md Documentation

A new section is added to `.github/skills/twig-sdlc/SKILL.md` documenting:
- The frontmatter schema (fields, types, requirements)
- How plan detection uses frontmatter for matching
- The fallback behavior when frontmatter is absent
- Instructions for the architect agent to include frontmatter in generated plans

### Design Decisions

1. **YAML frontmatter over HTML comments**: YAML frontmatter is the standard mechanism
   for metadata in Markdown files (used by Jekyll, Hugo, Docusaurus, GitHub Pages, etc.).
   HTML comments (`<!-- work_item_id: 1858 -->`) would be invisible in rendered output
   but harder to parse and non-standard for this purpose.

2. **Additive, not replacing**: The existing metadata tables/blockquotes are preserved.
   The frontmatter adds a machine-readable layer; the human-readable tables remain for
   visual scanning. This avoids disrupting the existing reading experience.

3. **Integer work_item_id, not string**: The work item ID is stored as a bare integer
   (not `#1858` or `"1858"`) to simplify numeric comparison in the detector.

4. **Simple line-by-line parsing**: The frontmatter parser uses the same line-by-line
   approach already established in `Get-PlanTitle` rather than importing a YAML library.
   This keeps the script dependency-free.

## Dependencies

### Internal Dependencies
- **docs/projects/*.plan.md**: All 16 plan files must be modified
- **tools/seed-from-plan.ps1**: Script must be updated
- **.github/skills/twig-sdlc/SKILL.md**: Documentation must be updated

### External Dependencies
- **twig-conductor-workflows** (external repo): The `plan_detector` agent must be
  updated to read frontmatter — this is out of scope but is the downstream consumer
  of the data contract established here

### Sequencing Constraints
- Task #1919 (frontmatter retrofit) should be done first — it establishes the data
- Task #1920 (script update) depends on the frontmatter schema being defined
- Task #1921 (SKILL.md update) can be done in parallel with #1920

## Impact Analysis

### Components Affected
- **16 plan files**: Additive frontmatter insertion — no content changes
- **seed-from-plan.ps1**: New function + modified function — backward compatible
- **SKILL.md**: Documentation addition — no behavioral change

### Backward Compatibility
- Plan files without frontmatter continue to work (all parsers fall back gracefully)
- `Get-PlanTitle` priority change (title > goal > H1) is compatible — no existing
  plan files use both `title:` and `goal:` frontmatter fields
- `Get-PlanWorkItemId` returns `$null` for files without frontmatter

## Open Questions

1. **Low**: Should `status` and `revision` fields be included in frontmatter?
   Currently these are in the human-readable metadata table. Adding them to
   frontmatter would enable machine reading but increases the maintenance surface.
   **Recommendation**: Out of scope — only the three detection-critical fields
   (`work_item_id`, `title`, `type`) are included.

2. **Low**: Should the two duplicate plan files for #1858 (`az-cli-timeout-override.plan.md`
   and `azcli-auth-timeout.plan.md`) be consolidated? They both target the same work item.
   **Recommendation**: Out of scope — both get frontmatter, dedup is a separate concern.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `docs/projects/auto-tag-twig-created-items.plan.md` | Add YAML frontmatter: `work_item_id: 1890`, `title`, `type: Issue` |
| `docs/projects/az-cli-timeout-override.plan.md` | Add YAML frontmatter: `work_item_id: 1858`, `title`, `type: Issue` |
| `docs/projects/azcli-auth-timeout.plan.md` | Add YAML frontmatter: `work_item_id: 1858`, `title`, `type: Issue` |
| `docs/projects/batch-state-field-updates.plan.md` | Add YAML frontmatter: `work_item_id: 1782`, `title`, `type: Issue` |
| `docs/projects/child-state-verification-gate.plan.md` | Add YAML frontmatter: `work_item_id: 1622`, `title`, `type: Task` |
| `docs/projects/companion-auto-upgrade.plan.md` | Add YAML frontmatter: `work_item_id: 1643`, `title`, `type: Issue` |
| `docs/projects/expand-mcp-tools.plan.md` | Add YAML frontmatter: `work_item_id: 1814`, `title`, `type: Epic` |
| `docs/projects/guard-duplicate-work-item-creation.plan.md` | Add YAML frontmatter: `work_item_id: 1891`, `title`, `type: Issue` |
| `docs/projects/mcp-error-handling-html-guard.plan.md` | Add YAML frontmatter: `work_item_id: 1752`, `title`, `type: Issue` |
| `docs/projects/mcp-multi-workspace.plan.md` | Add YAML frontmatter: `work_item_id: 1754`, `title`, `type: Issue` |
| `docs/projects/mcp-read-write-tools-expansion.plan.md` | Add YAML frontmatter: `work_item_id: 1814`, `title`, `type: Epic` |
| `docs/projects/parent-state-propagation.plan.md` | Add YAML frontmatter: `work_item_id: 1855`, `title`, `type: Issue` |
| `docs/projects/read-only-lookup-tools.plan.md` | Add YAML frontmatter: `work_item_id: 1813`, `title`, `type: Issue` |
| `docs/projects/streamline-closeout-fast-path.plan.md` | Add YAML frontmatter: `work_item_id: 1744`, `title`, `type: Issue` |
| `docs/projects/sync-perf-optimization-v3.plan.md` | Add YAML frontmatter: `work_item_id: 1611`, `title`, `type: Epic` |
| `docs/projects/twig-vscode-extension.plan.md` | Add YAML frontmatter: `work_item_id: 1263`, `title`, `type: Epic` |
| `tools/seed-from-plan.ps1` | Add `Get-PlanWorkItemId` function; update `Get-PlanTitle` priority |
| `.github/skills/twig-sdlc/SKILL.md` | Add frontmatter schema documentation section |

## ADO Work Item Structure

### Issue #1918: Plan detector false positives

**Goal**: Eliminate false positive plan detection by adding YAML frontmatter identity
to all plan files and updating tooling to consume it.

**Prerequisites**: None

#### Tasks

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| #1919 | Retrofit all 16 existing `.plan.md` files with YAML frontmatter (`work_item_id`, `title`, `type`) | All 16 `docs/projects/*.plan.md` files | Small (~80 lines added across 16 files) | ✅ Done |
| #1920 | Update `seed-from-plan.ps1`: add `Get-PlanWorkItemId` function, extend `Get-PlanTitle` to prefer frontmatter `title` field | `tools/seed-from-plan.ps1` | Small (~30 lines) | TO DO |
| #1921 | Update `.github/skills/twig-sdlc/SKILL.md` to document plan file frontmatter requirements and detection behavior | `.github/skills/twig-sdlc/SKILL.md` | Small (~40 lines) | TO DO |

**Acceptance Criteria**:
- [ ] All 16 `.plan.md` files have valid YAML frontmatter with `work_item_id`, `title`, `type`
- [ ] `Get-PlanWorkItemId` correctly extracts work item ID from frontmatter
- [ ] `Get-PlanWorkItemId` returns `$null` for files without frontmatter
- [ ] `Get-PlanTitle` prefers `title:` frontmatter over `goal:` and H1 heading
- [ ] `Get-PlanTitle` backward-compatible for files without `title:` frontmatter
- [ ] SKILL.md documents the frontmatter schema for the architect agent
- [ ] SKILL.md documents plan detection behavior (frontmatter match vs content search)

### Task #1919: Retrofit frontmatter on existing plan files (Done)

Add YAML frontmatter block to the top of each of the 16 existing `.plan.md` files.
The frontmatter contains three fields extracted from the existing metadata:

| Plan File | `work_item_id` | `title` | `type` |
|-----------|---------------|---------|--------|
| `auto-tag-twig-created-items.plan.md` | 1890 | Auto-Tag All Twig-Created Work Items with 'twig' Tag | Issue |
| `az-cli-timeout-override.plan.md` | 1858 | AzCliAuthProvider Timeout Override | Issue |
| `azcli-auth-timeout.plan.md` | 1858 | AzCliAuthProvider Timeout Increase & Configurability | Issue |
| `batch-state-field-updates.plan.md` | 1782 | Batch State Transitions and Field Updates | Issue |
| `child-state-verification-gate.plan.md` | 1622 | Child-State Verification Gate | Task |
| `companion-auto-upgrade.plan.md` | 1643 | Companion Auto-Upgrade | Issue |
| `expand-mcp-tools.plan.md` | 1814 | Expand twig-mcp with Read and Write Tools | Epic |
| `guard-duplicate-work-item-creation.plan.md` | 1891 | Guard Against Duplicate Work Item Creation Across SDLC Retries | Issue |
| `mcp-error-handling-html-guard.plan.md` | 1752 | MCP Error Handling & ADO HTML Response Guard | Issue |
| `mcp-multi-workspace.plan.md` | 1754 | MCP Multi-Workspace Support | Issue |
| `mcp-read-write-tools-expansion.plan.md` | 1814 | Expand twig-mcp with Read & Write Tools | Epic |
| `parent-state-propagation.plan.md` | 1855 | Parent State Propagation on First Task Start | Issue |
| `read-only-lookup-tools.plan.md` | 1813 | Read-Only Lookup Tools: twig_show, twig_children, twig_parent | Issue |
| `streamline-closeout-fast-path.plan.md` | 1744 | Streamline Close-Out Agent Fast-Path | Issue |
| `sync-perf-optimization-v3.plan.md` | 1611 | Sync Performance Optimization v3 | Epic |
| `twig-vscode-extension.plan.md` | 1263 | Twig VS Code Extension | Epic |

### Task #1920: Update seed-from-plan.ps1

**Implementation steps:**

1. Add `Get-PlanWorkItemId` function immediately after `Get-PlanTitle`:
   - Parse YAML frontmatter for `work_item_id:` field
   - Return `[int]` or `$null`
   - Same line-by-line parsing pattern as existing `Get-PlanTitle`

2. Update `Get-PlanTitle` to check for `title:` frontmatter field:
   - Insert `title:` check before the existing `goal:` check in the frontmatter loop
   - Priority: `title:` > `goal:` > H1 heading > filename
   - If both `title:` and `goal:` exist, `title:` wins

### Task #1921: Update SKILL.md documentation

**Add a new section** after the "Phases" section (or within Phase 2 Planning) covering:

1. **Plan File Frontmatter** — schema definition with field descriptions
2. **Plan Detection Behavior** — how the detector matches plans:
   - Primary: YAML frontmatter `work_item_id` exact match
   - Fallback: content search (deprecated, lower confidence)
3. **Architect Agent Requirements** — the architect must include frontmatter in generated plans
4. **Example** — complete frontmatter block showing all three fields

## PR Groups

### PG-1: Script and documentation updates

**Tasks**: #1920, #1921
**Classification**: Wide (3 files, mechanical changes)
**Estimated LoC**: ~70 lines changed
**Estimated Files**: 2 files (`tools/seed-from-plan.ps1`, `.github/skills/twig-sdlc/SKILL.md`)
**Predecessor**: #1919 (already Done — frontmatter must exist before script/docs update)

**Note**: Task #1919 is already Done and was committed separately. This PR group contains
the remaining two tasks which are logically coupled — the script reads frontmatter that
the SKILL.md documents.

## References

- [ADO Work Item #1918](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1918)
- [twig-conductor-workflows](https://github.com/PolyphonyRequiem/twig-conductor-workflows) — external repo containing `plan_detector` agent
- [YAML Frontmatter specification](https://jekyllrb.com/docs/front-matter/) — de facto standard
