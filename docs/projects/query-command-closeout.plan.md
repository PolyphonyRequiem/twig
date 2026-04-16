# Query Command (Epic 1302) Closeout Findings

> **Status**: ✅ Done
> **Issue**: #1634 | **Parent Epic**: #1302
> **Revision**: 17
>
> Issue #1634 is the parent work item for this closeout effort. It lives under
> Epic #1302 (Query Command) and contains three child Issues in ADO: #1635
> (note flushing semantics), #1636 (close-out checklist template), and #1637
> (PR grouping guidance). Each child Issue maps to one Issue section in this plan.

---

## Executive Summary

This plan addresses three closeout findings from the Query Command epic (#1302): (1) standardizing note flushing semantics to resolve the behavioral inconsistency between push-on-write and stage-and-batch patterns in `twig note`, (2) adding a formal close-out checklist section to the plan template so future epics have structured wrap-up guidance, and (3) documenting PR grouping strategy — specifically the finding that 2 PRs is the sweet spot per epic and 3 PRs is the threshold where cognitive overhead increases meaningfully. All three tasks are documentation and process improvements with minimal code changes — the primary deliverables are XML doc comments, copilot instruction files, and template updates.

---

## Background

### Current State

#### Note Flushing Architecture

The twig CLI underwent a major architectural transformation in Epic #1338 (Push-on-Write and Sync Convergence), which moved `NoteCommand` and `EditCommand` from a stage-then-save model to push-on-write with automatic cache resync. The current implementation of `twig note` follows a hybrid pattern:

- **Non-seed items**: Push-on-write via `AddCommentAsync()` with offline fallback to local staging
- **Seed items**: Always stage locally (seeds have no ADO identity to push to)
- **Auto-flush**: `update`, `state`, and `edit` commands auto-push any pending notes via `AutoPushNotesHelper.PushAndClearAsync()`
- **Batch flush**: `PendingChangeFlusher.FlushAsync()` handles notes during `twig save`, `twig sync`, and `twig flow-done`

While the push-on-write convergence resolved the primary inconsistency (notes no longer require explicit `twig save`), the code lacks clear documentation of the canonical behavior. Three distinct flush paths exist, and the relationship between them is implicit rather than documented. Compounding this, `NoteCommand`'s existing XML doc summary is **factually incorrect** post-push-on-write — for non-seed items, notes are pushed immediately via `AddCommentAsync()`, not stored as pending. The verbatim incorrect summary:

> `"Implements twig note ["text"]: adds a note to the active work item. If text is provided inline, stores as pending. Otherwise launches editor."`

#### Plan Template System

The project uses `.plan.md` documents in `docs/projects/` for feature planning, with 18 active plans and 51 archived plan documents (plus 4 supporting documents: PRDs, design docs, and scenario files). A formal template exists at `.github/templates/implementation-plan.md` with phases, testing strategy, rollout plan, and success metrics — but it lacks a close-out/completion section. In practice, completed plans have organically developed a `## Completion` section with date, PR references, summary, and final task status. This pattern is not codified in the template.

#### PR Grouping Documentation

PR grouping guidance is distributed across individual plan documents (10 plans contain PR group sections) but no central reference document exists. Plans use `## PR Groups` sections with Deep/Wide classification and sizing guardrails (≤2000 LoC, ≤50 files), but the strategic insight about optimal PR count per epic is undocumented. The `.github/copilot-instructions.md` file does not currently reference PR groups — the guidance exists only in per-plan sections and in the system prompts provided to planning agents at runtime.

---

## Problem Statement

1. **Misleading note flushing documentation**: The `NoteCommand` has three distinct code paths (push-on-write, offline fallback staging, seed staging) and three separate flush mechanisms (`NoteCommand` direct push, `AutoPushNotesHelper` auto-flush, `PendingChangeFlusher` batch flush). The canonical behavior is correct but the existing documentation is **actively misleading** — `NoteCommand`'s XML doc summary is factually incorrect post-push-on-write (see *Background § Note Flushing Architecture* for the verbatim text). For non-seed items, notes are pushed immediately via `AddCommentAsync()`, not stored as pending. Beyond the incorrect summary, there are no `<remarks>` blocks explaining *why* the hybrid pattern exists or *when* each path activates. A developer encountering the code for the first time would be misled by the existing docs and could not determine the intended semantics without reading all three components together.

2. **No close-out checklist in plan template**: The formal template at `.github/templates/implementation-plan.md` ends at "Notes" with no close-out guidance. Eight completed plans have added ad-hoc `## Completion` sections organically, but the format varies. Without a template section, epic close-out depends on tribal knowledge — agents and developers skip close-out steps or perform them inconsistently.

3. **Undocumented PR grouping strategy**: The "2-PR sweet spot, 3-PR threshold" insight exists only in verbal/session knowledge. Current plans use PR groups mechanically (Deep/Wide classification, LoC limits) but don't address the strategic question of *how many* PR groups an epic should target. This leads to plans that fragment work into 4+ PRs unnecessarily, increasing review overhead.

## Goals and Non-Goals

### Goals

| ID | Goal | FRs |
|----|------|-----|
| G-1 | Add comprehensive XML doc comments to `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` documenting the canonical note flushing semantics | FR-1, FR-2, FR-3 |
| G-2 | Add a `## Close-Out Checklist` section to `.github/templates/implementation-plan.md` | FR-4 |
| G-3 | Create `.github/instructions/pr-grouping.instructions.md` with PR grouping strategy guidance | FR-5 |
| G-4 | Update `.github/copilot-instructions.md` to reference the new PR grouping instructions — currently the file contains zero PR group references, so this introduces the cross-link for the first time | FR-6 |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG-1 | Refactoring the note flushing implementation — the current hybrid pattern is intentional and correct; only documentation is needed |
| NG-2 | Changing the `AutoPushNotesHelper` error handling gradient (EditCommand catches both auto-push and resync; StateCommand propagates auto-push but catches resync; UpdateCommand propagates both) — this is a known design decision documented in Proposed Design (see *Note Flushing Error Handling Policy*) |
| NG-3 | Migrating existing completed plans to the new close-out format — the template applies prospectively |
| NG-4 | Creating automated tooling to enforce PR group count — guidance is advisory, not prescriptive |

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `NoteCommand` XML doc comment replaces the incorrect summary (see *Background § Note Flushing Architecture* for the verbatim text) and explains the three code paths: push-on-write (non-seed, online), offline fallback (non-seed, ADO unreachable), and local staging (seed). The existing summary must be corrected, not merely augmented. |
| FR-2 | `AutoPushNotesHelper` XML doc comment explains it as a side-effect flusher triggered by other commands, not a user-facing operation |
| FR-3 | `PendingChangeFlusher` note handling documented to explain it as the batch path for `save`/`sync`/`flow-done`, covering residual staged notes |
| FR-4 | Plan template `## Close-Out Checklist` section includes: completion date, PR references, summary narrative, final task status table, and ADO state transition checklist |
| FR-5 | PR grouping instructions document the 2-PR sweet spot, 3-PR threshold, Deep/Wide classification, and sizing guardrails |
| FR-6 | Copilot instructions reference the PR grouping document for plan generation |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All XML doc comment changes (Issue #1635) must compile cleanly under `TreatWarningsAsErrors=true`. XML doc elements (`<remarks>`, `<list>`, `<item>`, `<para>`, `<c>`, `<see>`) must be well-formed — any CS1570/CS1571 warning will break the build. This constraint applies to T-1635-1, T-1635-2, and T-1635-3. |

---

## Proposed Design

### Architecture Overview

This plan modifies three independent surface areas — all documentation, no runtime code. The note flushing documentation (Issue #1635) adds XML doc comments to three existing components (`NoteCommand`, `AutoPushNotesHelper`, `PendingChangeFlusher`) that together form the note lifecycle: `NoteCommand` is the user-facing entry point, `AutoPushNotesHelper` is a side-effect flusher invoked by other commands, and `PendingChangeFlusher` is the batch fallback path. The close-out template (Issue #1636) extends the plan template at `.github/templates/implementation-plan.md` with a single new terminal section. The PR grouping guidance (Issue #1637) introduces a new instructions file at `.github/instructions/pr-grouping.instructions.md` and cross-links it from `.github/copilot-instructions.md`. No component depends on another — all three can be implemented and reviewed independently.

### Key Components

#### Note Flushing Documentation Model

The XML doc comments on `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` will document the dual-path model using `<remarks>` blocks: push-on-write (non-seed, online) flows through `AddCommentAsync()` directly; the fallback path (offline or seed) stages locally via `StageLocallyAsync()` and is drained later by `AutoPushNotesHelper` (triggered by `update`/`state`/`edit`) or `PendingChangeFlusher` (triggered by `save`/`sync`/`flow-done`). See *Background § Note Flushing Architecture* for the full flow.

#### Close-Out Checklist Template

The new `## Close-Out Checklist` section will be added to `.github/templates/implementation-plan.md` after the existing `## Notes` section (at the end of the template). This placement matches the established convention: all 8 completed plans appended their `## Completion` sections at the document's end, after all planning content. Placing close-out after Notes preserves the natural chronological flow — planning sections (phases, metrics, timeline) come first, followed by the post-completion record.

#### PR Grouping Strategy Instructions

A new `.github/instructions/pr-grouping.instructions.md` file will provide centralized PR grouping guidance, replacing the fragmented per-plan descriptions. Key content areas:

1. **What PR groups are** — cross-cutting overlay, not 1:1 with ADO hierarchy
2. **Sizing guardrails** — ≤2000 LoC, ≤50 files per group
3. **Deep vs Wide classification** — when to use each, review implications
4. **Optimal count guidance** — 2-PR sweet spot, 3-PR threshold, rationale
5. **Naming convention** — PG-N (not PR-N) to avoid GitHub PR number confusion
6. **Anti-patterns** — over-fragmentation, under-grouping

### Note Flushing Error Handling Policy

The current `AutoPushNotesHelper` integration exhibits an intentional error-handling gradient across its three call sites. This gradient is a known design decision (see NG-2) and should be documented, not changed. The three commands form a 3-level leniency gradient:

| Call Site | `AutoPushNotesHelper` Failure | Post-Push Resync Failure | Leniency |
|-----------|-------------------------------|--------------------------|----------|
| `EditCommand` | Caught (non-cancellation exceptions swallowed, lines 118–125) | Caught (try-catch around `FetchAsync`/`SaveAsync`, lines 128–135 — warns "cache may be stale") | Most lenient |
| `StateCommand` | Propagates (no try-catch, line 109) | Caught (try-catch around `FetchAsync`/`SaveAsync`, lines 113–121 — warns "cache may be stale") | Middle |
| `UpdateCommand` | Propagates (no try-catch, line 113) | Propagates (no try-catch, lines 115–116 — resync failure is fatal) | Strictest |

The rationale follows a 3-level gradient aligned with each command's error-handling philosophy:

- **EditCommand (most lenient)**: The user's edit must succeed even if a stale note can't be pushed or the cache can't be refreshed. Both `AutoPushNotesHelper` and post-push resync are wrapped in separate try-catch blocks (lines 118–125, 128–135 respectively). Auto-push failures produce a warning ("Note push failed (fields already pushed)"); resync failures produce a warning ("Changes pushed but cache may be stale — run 'twig sync' to resync"). Neither failure stages locally or aborts the command.
- **StateCommand (middle)**: The state transition is the user's primary intent and is already committed to ADO before auto-push runs. `AutoPushNotesHelper` is called without try-catch (line 109), so note push failures propagate. However, post-push resync is wrapped in try-catch (lines 113–121) because a stale cache is tolerable when the state transition succeeded.
- **UpdateCommand (strictest)**: No error suppression. Both `AutoPushNotesHelper` (line 113) and resync (`FetchAsync`/`SaveAsync`, lines 115–116) are called without try-catch, so any failure propagates to the user.

This policy must be documented in `AutoPushNotesHelper`'s XML doc `<remarks>` block (T-1635-2) so that future developers understand the gradient is intentional and consistent with each command's error-handling philosophy.

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Close-out checklist placed after Notes (at end of template) | Matches established convention — all 8 completed plans appended `## Completion` at the document's end. End-of-document placement preserves the natural lifecycle: plan → execute → close out. |
| DD-2 | PR grouping guidance as a `.instructions.md` file (not in copilot-instructions.md) | Keeps `copilot-instructions.md` focused on project-level constraints. Instructions files are purpose-specific and auto-loaded by Copilot agents. The `.github/instructions/` directory already contains 3 established files (`documentation.instructions.md`, `kusto-toolkit.instructions.md`, `telemetry.instructions.md`), validating this as the standard convention for domain-specific guidance. See *Alternatives Considered § DD-2*. |
| DD-3 | Use PG-N naming convention for PR groups | Avoids confusion with GitHub PR numbers. `PG` is distinct and reads naturally as "PR Group". `PR-N` is ambiguous with GitHub PR numbers; `PRG-N` is non-standard and less readable. |

---

## Alternatives Considered

### DD-2: PR Grouping Guidance Location

**Chosen**: Create a dedicated `.github/instructions/pr-grouping.instructions.md` file with a cross-link from `copilot-instructions.md`.

**Alternative — Embed guidance directly in `copilot-instructions.md`**: Rejected — that file is for project-level constraints (AOT rules, coding conventions, telemetry policy). The `.github/instructions/` directory already hosts 3 established files establishing the convention for domain-specific guidance.

**Alternative — Wiki or external documentation**: Rejected — wiki content is not version-controlled, cannot be referenced by Copilot agents, and creates a synchronization burden.

---

## Dependencies

None. All three Issues are documentation-only changes with no external library, service, or infrastructure dependencies. No internal component changes are required — only XML doc comments, Markdown templates, and instruction files are affected. The three Issues are mutually independent and can be implemented in any order.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| XML doc `<remarks>` and `<list>` elements produce CS1570/CS1571 warnings, breaking the build under `TreatWarningsAsErrors=true` (NFR-1) | Medium | High | Validate XML doc syntax by running `dotnet build` after each T-1635 task. Use only well-formed XML elements (`<para>`, `<list>`, `<item>`, `<c>`) and avoid bare angle brackets or unescaped ampersands in doc comments. Review compiler output for CS1570/CS1571 specifically before committing. |
| T-1637-2 modifies `.github/copilot-instructions.md`, which may conflict with concurrent plan work editing the same file | Medium | Low | PG-2 (containing T-1637-2) is independent of PG-1 and can be rebased independently. Merge PG-2 early or coordinate with any in-flight work that also modifies `copilot-instructions.md`. The edit is an additive subsection insertion, minimizing merge conflict surface. |

---

## Open Questions

None. All design decisions are resolved:

- **Note flushing model**: The push-on-write vs stage-and-batch semantics are established by the existing code and documented in *Proposed Design § Note Flushing Error Handling Policy*. No design choice is required — this plan documents the existing behavior.
- **Close-out template placement**: DD-1 resolved this — the `## Close-Out Checklist` section goes after `## Notes` at the end of the template, matching the organic convention from 8 completed plans.
- **PR grouping guidance location**: DD-2 resolved this — a dedicated `.github/instructions/pr-grouping.instructions.md` file, consistent with the 3 existing files in that directory.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `.github/instructions/pr-grouping.instructions.md` | Centralized PR grouping strategy guidance (T-1637-1) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/NoteCommand.cs` | Replace incorrect XML doc summary; add `<remarks>` documenting three code paths (T-1635-1) |
| `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs` | Enhance XML doc comment with side-effect flusher role, `<remarks>`, and error-handling asymmetry across call sites (T-1635-2) |
| `src/Twig/Commands/PendingChangeFlusher.cs` | Add `<remarks>` to `FlushAsync` documenting note batch flush as fallback path (T-1635-3) |
| `src/Twig/Commands/IPendingChangeFlusher.cs` | Update `FlushAsync` XML doc summary to mention notes explicitly (T-1635-3) |
| `.github/templates/implementation-plan.md` | Add `## Close-Out Checklist` section at end of template (T-1636-1) |
| `.github/copilot-instructions.md` | Add reference to PR grouping instructions file; fix dangling `process-agnostic.instructions.md` reference (T-1637-2) |

---

## ADO Work Item Structure

### Issue #1635: Standardize note flushing semantics (push-on-write vs stage-and-batch)

**Goal**: Document the canonical note flushing model across all code paths so the push-on-write vs stage-and-batch behavior is explicit and unambiguous. *(Supports G-1)*

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1635-1 | Replace incorrect `NoteCommand` XML doc summary; add `<remarks>` documenting three code paths and component relationships | `src/Twig/Commands/NoteCommand.cs` | Small |
| T-1635-2 | Enhance `AutoPushNotesHelper` XML doc with side-effect flusher role and error-handling asymmetry documentation | `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs` | Small |
| T-1635-3 | Add note-specific `<remarks>` to `FlushAsync`; update `IPendingChangeFlusher.FlushAsync` summary to mention notes | `src/Twig/Commands/PendingChangeFlusher.cs`, `src/Twig/Commands/IPendingChangeFlusher.cs` | Small |

**T-1635-1 Details**: Replace the factually incorrect XML doc summary on `NoteCommand` (see *Background § Note Flushing Architecture* for the verbatim text). Add a `<remarks>` block documenting: (a) the three code paths — push-on-write (non-seed, online), offline fallback via `StageLocallyAsync()` (non-seed, ADO unreachable), and seed staging; (b) the inner try-catch around post-push resync that makes resync failure non-fatal; and (c) the relationship to `AutoPushNotesHelper` and `PendingChangeFlusher`.

**T-1635-2 Details**: Document `AutoPushNotesHelper`'s role as a side-effect flusher triggered by `update`/`state`/`edit`, not a user-facing operation. It is a `static class` — use class-level `<summary>` and `<remarks>`. Add `<remarks>` explaining when notes accumulate in `pending_changes` and documenting the error-handling gradient across call sites. See *Proposed Design § Note Flushing Error Handling Policy* for the full gradient details — both dimensions (whether auto-push failures propagate and whether resync failures propagate) must be documented, forming the 3-level leniency scale.

**T-1635-3 Details**: `PendingChangeFlusher` already has substantial class-level `<remarks>` covering continue-on-failure semantics, notes-only conflict bypass, and cache resync; `IPendingChangeFlusher` already documents callers (`SaveCommand`, `SyncCommand`, `FlowDoneCommand`) in its class-level `<summary>`. This task adds only what is missing: a `<remarks>` note on `FlushAsync` clarifying that the note batch flush path is the fallback for residual staged notes (from offline fallback or seed publishing), and updating the `FlushAsync` summary to mention notes explicitly. Both `IPendingChangeFlusher.cs` and `PendingChangeFlusher.cs` share the same wording and both need the notes reference added.

**Acceptance Criteria**:
- [ ] All XML doc comments compile cleanly under `TreatWarningsAsErrors=true`
- [ ] The incorrect `"stores as pending"` summary on `NoteCommand` is replaced with accurate push-on-write documentation (see *Background § Note Flushing Architecture* for the verbatim incorrect text)
- [ ] A developer reading `NoteCommand.cs` can determine the canonical flushing model without reading other files
- [ ] The relationship between `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` is documented in each component's XML docs
- [ ] `FlushAsync` `<remarks>` on `PendingChangeFlusher` mentions `FlowDoneCommand` as a caller alongside `SaveCommand` and `SyncCommand` (note: `IPendingChangeFlusher`'s class-level summary already lists these callers — do not duplicate there)
- [ ] `IPendingChangeFlusher.FlushAsync` XML doc summary mentions notes explicitly (not just "pending changes")
- [ ] `dotnet build` and `dotnet test` pass — existing tests in `AutoPushNotesHelperTests.cs` and `PendingChangeFlusherTests.cs` serve as regression validation

---

### Issue #1636: Add close-out checklist section to plan template

**Goal**: Formalize the close-out pattern observed in completed plans into the official plan template. *(Supports G-2)*

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1636-1 | Add `## Close-Out Checklist` section to `.github/templates/implementation-plan.md` after the existing `## Notes` section (at the end of the template), with: completion date placeholder, PR references table, summary narrative placeholder, final task status table, and ADO state transition checklist | `.github/templates/implementation-plan.md` | Small |

**T-1636-1 Sample Structure** — the template section will follow this layout:

```markdown
## Close-Out Checklist

### Completion Summary

**Completed**: [YYYY-MM-DD]

[1–2 paragraph narrative: what was delivered, key decisions made, lessons learned]

### Pull Requests

| PR | Title | Status |
|----|-------|--------|
| #NNN | [PR title] | Merged / Open |

### Final Task Status

| Task | Description | Status |
|------|-------------|--------|
| T-X-1 | [Description] | ✅ Done |

### ADO Close-Out

- [ ] All child Tasks transitioned to Done (`twig set <id>` → `twig state Done`)
- [ ] All child Issues transitioned to Done
- [ ] Parent Epic transitioned to Done
- [ ] `git push` completed — all commits on remote
```

**Acceptance Criteria**:
- [ ] Template contains a `## Close-Out Checklist` section with all required subsections
- [ ] The checklist includes ADO state transition steps (`twig state Done` for tasks, issues, and parent epic)
- [ ] The checklist includes PR reference fields (PR number, title, status)
- [ ] The format is consistent with the organic pattern established by 8 completed plans

---

### Issue #1637: Document PR grouping guidance (2-PR sweet spot, 3-PR threshold)

**Goal**: Create a centralized PR grouping strategy document and integrate it into the Copilot agent instruction set. *(Supports G-3, G-4)*

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1637-1 | Create `.github/instructions/pr-grouping.instructions.md` with PR grouping strategy content (see Details below) | `.github/instructions/pr-grouping.instructions.md` | Medium |
| T-1637-2 | Add PR grouping reference to `.github/copilot-instructions.md`; fix dangling `process-agnostic.instructions.md` reference (see Details below) | `.github/copilot-instructions.md` | Small |

**T-1637-1 Details**: Create the centralized PR grouping instructions file with the following content areas: (1) PR group definition — a cross-cutting overlay for code review, not a 1:1 mapping to the ADO hierarchy; (2) sizing guardrails — ≤2000 LoC, ≤50 files per group; (3) Deep vs Wide classification — criteria for each type, review implications, and concrete examples from Twig plans; (4) optimal count guidance — the "2-PR sweet spot, 3-PR threshold" finding with rationale, explicitly noting that this heuristic derives from Twig's completed epics (a limited sample) and should be treated as advisory, not prescriptive; (5) PG-N naming convention — why PG-N over PR-N; and (6) anti-patterns — over-fragmentation (4+ PRs increasing context-switching overhead) and mega-PRs (single PR for entire epic reducing reviewability).

**T-1637-2 Details**: Add a new `### PR Grouping Strategy` subsection to `.github/copilot-instructions.md` inside the existing `## ADO Work Item Tracking` section, placed after the `### Commit conventions` subsection (which ends with the bullet *"If no mapping exists for the current work, commit normally without AB# reference"*) and before the `## Work Item Lifecycle Protocol` section. The subsection should contain a brief description of PR grouping purpose and a `See .github/instructions/pr-grouping.instructions.md for the full guide` reference. Separately, replace the dangling reference to `.github/instructions/process-agnostic.instructions.md` in the `## Project Overview` section (the line reading *"See `.github/instructions/process-agnostic.instructions.md` for the full principle."*) with inline text that conveys the principle without referencing a non-existent file.

**Acceptance Criteria**:
- [ ] A new `.github/instructions/pr-grouping.instructions.md` file exists with all required sections
- [ ] The document includes the "2-PR sweet spot, 3-PR threshold" finding with concrete rationale and a sample-size caveat
- [ ] Deep vs Wide classification criteria are clearly defined with examples
- [ ] `.github/copilot-instructions.md` contains a `### PR Grouping Strategy` subsection under `## ADO Work Item Tracking`, between `### Commit conventions` and `## Work Item Lifecycle Protocol`
- [ ] The guidance is advisory, not prescriptive — allows deviation with rationale
- [ ] The dangling `.github/instructions/process-agnostic.instructions.md` reference in `copilot-instructions.md` is replaced with inline text

---

## PR Groups

PR groups are classified as **deep** (few files, complex logic changes requiring careful review) or **wide** (many files, mechanical/repetitive changes that are straightforward to verify).

### PG-1: Note Flushing Documentation + Close-Out Template

| Attribute | Value |
|-----------|-------|
| **Issues** | #1635, #1636 |
| **Tasks** | T-1635-1, T-1635-2, T-1635-3, T-1636-1 |
| **Classification** | Wide |
| **Estimated LoC** | ~120 (XML doc comments + template section) |
| **Files** | `src/Twig/Commands/NoteCommand.cs`, `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs`, `src/Twig/Commands/PendingChangeFlusher.cs`, `src/Twig/Commands/IPendingChangeFlusher.cs`, `.github/templates/implementation-plan.md` |
| **Rationale** | Both Issues #1635 and #1636 are documentation-only changes that share no code dependencies. Grouping them reduces PR overhead while keeping the review focused on documentation quality. The changes are mechanical (XML doc comments, template additions) and spread across many files — a classic "wide" PR. |

### PG-2: PR Grouping Instructions

| Attribute | Value |
|-----------|-------|
| **Issues** | #1637 |
| **Tasks** | T-1637-1, T-1637-2 |
| **Classification** | Deep |
| **Estimated LoC** | ~150 (new instructions file + copilot-instructions update) |
| **Files** | `.github/instructions/pr-grouping.instructions.md` (new), `.github/copilot-instructions.md` |
| **Rationale** | The PR grouping instructions document is a standalone artifact that benefits from focused review. It introduces new strategic guidance (2-PR sweet spot, 3-PR threshold) that reviewers should evaluate holistically. Keeping it separate from the mechanical documentation changes in PG-1 ensures the strategic content gets appropriate attention. |

### Execution Order

PG-1 and PG-2 are independent and can be developed, reviewed, and merged in parallel. Neither is a prerequisite of the other.

---

## Revision History

| Rev | Changes |
|-----|---------|
| 17 | Addressed review feedback (tech=88, read=82). **(1)** Fixed critical factual error in Note Flushing Error Handling Policy table: EditCommand **does** resync (lines 128–135 with try-catch), not "N/A". Rewrote table and rationale to reflect the full 3-level leniency gradient (EditCommand most lenient → StateCommand middle → UpdateCommand strictest). Updated NG-2 and T-1635-2 details to use "gradient" terminology consistently. **(2)** Added `## Dependencies` section (None — documented rationale for independence). **(3)** Added `## Open Questions` section (None — all design decisions resolved, with per-question rationale). **(4)** Elevated `TreatWarningsAsErrors=true` build constraint from prose in Risks table to explicit NFR-1 in Non-Functional Requirements. **(5)** Corrected archived document count: "55 archived project documents" → "51 archived plan documents (plus 4 supporting documents)" reflecting actual `.plan.md` count vs total file count. |