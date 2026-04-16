# Query Command (Epic 1302) Closeout Findings

> **Status**: ✅ Done — All PRs merged | **Issue**: #1634 | **Parent Epic**: #1302
>
> Issue #1634 is the parent work item for this closeout effort. It lives under
> Epic #1302 (Query Command) and contains three child Tasks in ADO: #1635
> (note flushing semantics), #1636 (close-out checklist template), and #1637
> (PR grouping guidance). Each child Task maps to one Issue section in this plan.

---

## Executive Summary

This plan addresses three closeout findings from the Query Command epic (#1302): (1) standardizing note flushing semantics by correcting misleading XML doc comments and documenting the push-on-write vs stage-and-batch dual-path model, (2) adding a close-out checklist section to the plan template for structured wrap-up guidance, and (3) documenting PR grouping strategy — the "2-PR sweet spot, 3-PR threshold" heuristic. All deliverables are documentation and process improvements: XML doc comments, a copilot instruction file, and a template update. Work was delivered across two PRs on separate branches and merged to `main`.

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

The project uses `.plan.md` documents in `docs/projects/` for feature planning, with 18 active plans and 51 archived plans. Alongside the plans, there are 11 supporting documents across both directories (7 active, 4 archived) — PRDs, requirements, design docs, scenario maps, audit artifacts, and code references. A formal template exists at `.github/templates/implementation-plan.md` with phases, testing strategy, rollout plan, and success metrics — but it lacks a close-out/completion section. In practice, completed plans have organically developed a `## Completion` section with date, PR references, summary, and final task status. This pattern is not codified in the template.

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
| NFR-1 | All XML doc comment changes (Issue #1635) must compile cleanly under `TreatWarningsAsErrors=true`. XML doc elements (`<remarks>`, `<list>`, `<item>`, `<para>`, `<c>`, `<see>`) must be well-formed — any CS1570/CS1571 warning will break the build. Applies to T-1635-1, T-1635-2, and T-1635-3. |

---

## Proposed Design

### Architecture / Approach Overview

This is a documentation-only effort — no runtime behavior changes. The approach is to add XML doc comments to three existing components (`NoteCommand`, `AutoPushNotesHelper`, `PendingChangeFlusher`) that together implement the note flushing subsystem, update the plan template with a formalized close-out checklist section, and create a centralized PR grouping strategy document cross-linked from `.github/copilot-instructions.md`. Each deliverable is independently committable and testable. The XML doc changes must compile cleanly under `TreatWarningsAsErrors=true` (NFR-1); the template and instructions changes are pure Markdown with no build validation. Note that `StageLocallyAsync()` is `private` in `NoteCommand`, which limits `<see cref=""/>` cross-reference options — the XML docs use descriptive text references instead of code links for that method.

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

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | **Close-Out Checklist placement**: append after `## Notes` at the end of the plan template | Matches the organic convention established by all 8 completed plans, which appended `## Completion` sections at the document's end. Preserves natural chronological flow — planning sections first, post-completion record last. |
| DD-2 | **Dangling `process-agnostic.instructions.md` reference**: replace with inline text in `copilot-instructions.md` rather than creating the missing file | The principle is already stated inline (three lines above the reference). Creating a dedicated instructions file for a single sentence adds unnecessary indirection. Inline resolution eliminates the dangling reference without introducing a new file that would need maintenance. |
| DD-3 | **`<inheritdoc/>` on `PendingChangeFlusher.FlushAsync`** rather than duplicating interface docs | Standard C# pattern for concrete implementations. Prevents doc comment drift between interface and class. The interface (`IPendingChangeFlusher`) is the canonical documentation surface; the implementation inherits it. |
| DD-4 | **Error-handling gradient documented in situ** (in `AutoPushNotesHelper`'s XML doc), not refactored | The gradient across `EditCommand` (lenient), `StateCommand` (middle), and `UpdateCommand` (strict) is an intentional design choice documented in NG-2. Documenting the existing behavior in XML doc comments is the correct action; refactoring the error-handling is explicitly out of scope. |
| DD-5 | **Close-Out Checklist ADO items**: combine Tasks and Issues on one line as `"All child Tasks and Issues transitioned to Done"` | Aligns with the format already merged on `main` in the implementation-plan template (`.github/templates/implementation-plan.md`). Separate lines for Tasks and Issues create unnecessary verbosity for a single `twig state Done` operation. |

---

## Alternatives Considered

### DD-1: Close-Out Checklist Placement

| Option | Pros | Cons |
|--------|------|------|
| **After `## Notes` (chosen)** | Matches the organic convention from 8 completed plans; preserves natural chronological flow (planning → execution → close-out) | Placed at very end of document; might be overlooked during active planning |
| Before `## Notes` | Closer to the implementation sections; visible during planning phase | Breaks the temporal flow — close-out appears before the catch-all Notes section, contrary to established convention |
| Separate file (e.g., `CLOSEOUT.md`) | Clean separation of planning vs retrospective content | Introduces a second file per plan; complicates archival; no precedent in existing plans |

**Decision**: After `## Notes`, matching the convention already established organically by all 8 completed plans.

### DD-2: Dangling `process-agnostic.instructions.md` Reference

| Option | Pros | Cons |
|--------|------|------|
| **Inline text replacement (chosen)** | Eliminates the dangling reference immediately; the principle is already stated three lines above the reference in `copilot-instructions.md` | Slightly more text in the copilot instructions file |
| Create the missing `.instructions.md` file | Provides a dedicated, discoverable file for the principle | Adds a file containing a single sentence that duplicates existing inline text; introduces maintenance burden for zero incremental value |

**Decision**: Inline replacement — the principle is already stated in context, and a single-sentence instructions file adds unnecessary indirection.

---

## Open Questions

None. All design decisions are documented and implementation is complete.

---

## Dependencies

No external library, service, or infrastructure dependencies — all deliverables are documentation and template changes. No inter-issue dependencies exist: Issues #1635, #1636, and #1637 are fully independent and can be implemented, reviewed, and merged in any order. The only build-time constraint is NFR-1 (XML doc comments must compile cleanly under `TreatWarningsAsErrors=true`), which is validated by the existing `dotnet build` pipeline.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| XML doc `<remarks>` and `<list>` elements produce CS1570/CS1571 warnings, breaking the build under `TreatWarningsAsErrors=true` (NFR-1) | Medium | High | Validate XML doc syntax by running `dotnet build` after each T-1635 task. Use only well-formed XML elements (`<para>`, `<list>`, `<item>`, `<c>`) and avoid bare angle brackets or unescaped ampersands in doc comments. Review compiler output for CS1570/CS1571 specifically before committing. **Outcome**: Did not materialize — all XML docs compiled cleanly. |
| Branch `feature/1634-query-closeout` is 189 commits behind `main` — merge conflicts possible when creating PRs | Medium | Medium | Rebase or merge `main` into the feature branch before creating PRs. **Outcome**: Mitigated by delivering work on two separate branches (`users/dangreen/pg1-note-flushing-docs-closeout-template` for PG-1 and `feature/pg2-pr-grouping-instructions` for PG-2) rather than a single feature branch. Both merged cleanly to `main` as PRs #38 and #41. |

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
| `src/Twig/Commands/PendingChangeFlusher.cs` | Use `<inheritdoc/>` on `FlushAsync` to inherit interface docs; class-level `<remarks>` already document batch flush semantics (T-1635-3) |
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
| T-1635-3 | Add note-specific `<remarks>` to `IPendingChangeFlusher.FlushAsync`; use `<inheritdoc/>` on `PendingChangeFlusher.FlushAsync` | `src/Twig/Commands/PendingChangeFlusher.cs`, `src/Twig/Commands/IPendingChangeFlusher.cs` | Small |

**T-1635-1 Details**: Replace the factually incorrect XML doc summary on `NoteCommand` (see *Background § Note Flushing Architecture* for the verbatim text). Add a `<remarks>` block documenting: (a) the three code paths — push-on-write (non-seed, online), offline fallback via `StageLocallyAsync()` (non-seed, ADO unreachable), and seed staging; (b) the inner try-catch around post-push resync that makes resync failure non-fatal; and (c) the relationship to `AutoPushNotesHelper` and `PendingChangeFlusher`.

**T-1635-2 Details**: Document `AutoPushNotesHelper`'s role as a side-effect flusher triggered by `update`/`state`/`edit`, not a user-facing operation. It is a `static class` — use class-level `<summary>` and `<remarks>`. Add `<remarks>` explaining when notes accumulate in `pending_changes` and documenting the error-handling gradient across call sites. The gradient is 3 levels: `EditCommand` (most lenient — both auto-push and resync failures caught and warned), `StateCommand` (middle — auto-push failure propagates, resync failure caught), `UpdateCommand` (strictest — both failures propagate). Document both dimensions (auto-push and resync failure handling) for each call site.

**T-1635-3 Details**: `PendingChangeFlusher` already has substantial class-level `<remarks>` covering continue-on-failure semantics, notes-only conflict bypass, and cache resync; `IPendingChangeFlusher` already documents callers (`SaveCommand`, `SyncCommand`, `FlowDoneCommand`) in its class-level `<summary>`. This task adds only what is missing: a `<remarks>` note on `IPendingChangeFlusher.FlushAsync` clarifying that the note batch flush path is the fallback for residual staged notes (from offline fallback or seed publishing), and updating the `FlushAsync` summary to mention notes explicitly. `PendingChangeFlusher.FlushAsync` uses `<inheritdoc/>` to inherit the interface's documentation — this is the correct pattern for concrete implementations and avoids doc comment drift between interface and class.

**Acceptance Criteria** *(all met — delivered in PR #38)*:
- [x] All XML doc comments compile cleanly under `TreatWarningsAsErrors=true`
- [x] The incorrect `"stores as pending"` summary on `NoteCommand` is replaced with accurate push-on-write documentation (see *Background § Note Flushing Architecture* for the verbatim incorrect text)
- [x] A developer reading `NoteCommand.cs` can determine the canonical flushing model without reading other files
- [x] The relationship between `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` is documented in each component's XML docs
- [x] `FlowDoneCommand` is documented as a caller in `IPendingChangeFlusher`'s class-level `<summary>` alongside `SaveCommand` and `SyncCommand` — `PendingChangeFlusher.FlushAsync` uses `<inheritdoc/>` and does not duplicate caller references
- [x] `IPendingChangeFlusher.FlushAsync` XML doc summary mentions notes explicitly (not just "pending changes")
- [x] `dotnet build` and `dotnet test` pass — existing tests in `AutoPushNotesHelperTests.cs` and `PendingChangeFlusherTests.cs` serve as regression validation

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

- [ ] All child Tasks and Issues transitioned to Done (`twig set <id>` → `twig state Done`)
- [ ] Parent Epic transitioned to Done (`twig set <id>` → `twig state Done`)
- [ ] `git push` completed — all commits on remote
```

**Acceptance Criteria** *(all met — delivered in PR #38)*:
- [x] Template contains a `## Close-Out Checklist` section with all required subsections
- [x] The checklist includes ADO state transition steps (`twig state Done` for tasks, issues, and parent epic)
- [x] The checklist includes PR reference fields (PR number, title, status)
- [x] The format is consistent with the organic pattern established by 8 completed plans

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

**Acceptance Criteria** *(all met — delivered in PR #41)*:
- [x] A new `.github/instructions/pr-grouping.instructions.md` file exists with all required sections
- [x] The document includes the "2-PR sweet spot, 3-PR threshold" finding with concrete rationale and a sample-size caveat
- [x] Deep vs Wide classification criteria are clearly defined with examples
- [x] `.github/copilot-instructions.md` contains a `### PR Grouping Strategy` subsection under `## ADO Work Item Tracking`, between `### Commit conventions` and `## Work Item Lifecycle Protocol`
- [x] The guidance is advisory, not prescriptive — allows deviation with rationale
- [x] The dangling `.github/instructions/process-agnostic.instructions.md` reference in `copilot-instructions.md` is replaced with inline text

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

---

## Close-Out Checklist

### Completion Summary

**Implementation Completed**: 2026-04-16 | **PRs Merged**: 2026-04-16

All three Issues delivered documentation and process improvements with no runtime code changes. Issue #1635 corrected the factually incorrect `NoteCommand` XML doc summary and added comprehensive `<remarks>` blocks to `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher`, documenting the push-on-write vs stage-and-batch semantics and the error-handling gradient across call sites. Issue #1636 added a structured `## Close-Out Checklist` section to the plan template at `.github/templates/implementation-plan.md`, formalizing the close-out pattern observed in 8 completed plans. Issue #1637 created `.github/instructions/pr-grouping.instructions.md` with the "2-PR sweet spot, 3-PR threshold" heuristic and cross-linked it from `.github/copilot-instructions.md`.

**Branch note**: Implementation was delivered across two separate branches rather than the originally planned single `feature/1634-query-closeout` branch — PG-1 was implemented on `users/dangreen/pg1-note-flushing-docs-closeout-template` and PG-2 on `feature/pg2-pr-grouping-instructions`. Both branches were merged to `main` independently.

### Pull Requests

| PR | Title | Branch | PR Group | Status |
|----|-------|--------|----------|--------|
| #38 | Note flushing documentation + close-out template | `users/dangreen/pg1-note-flushing-docs-closeout-template` | PG-1 | ✅ Merged (2500bad) |
| #41 | PR grouping instructions | `feature/pg2-pr-grouping-instructions` | PG-2 | ✅ Merged (2e9073c) |

### Final Task Status

| Task | Description | Status |
|------|-------------|--------|
| T-1635-1 | Replace incorrect `NoteCommand` XML doc summary; add `<remarks>` | ✅ Done |
| T-1635-2 | Enhance `AutoPushNotesHelper` XML doc with error-handling gradient | ✅ Done |
| T-1635-3 | Add note-specific `<remarks>` to `IPendingChangeFlusher.FlushAsync`; use `<inheritdoc/>` on `PendingChangeFlusher.FlushAsync` | ✅ Done |
| T-1636-1 | Add `## Close-Out Checklist` to plan template | ✅ Done |
| T-1637-1 | Create `pr-grouping.instructions.md` | ✅ Done |
| T-1637-2 | Add PR grouping reference to `copilot-instructions.md` | ✅ Done |

### ADO Close-Out

- [x] All child Tasks and Issues transitioned to Done (`twig set <id>` → `twig state Done`)
- [x] Parent Epic #1302 transitioned to Done
- [x] `git push` completed — all commits on remote

