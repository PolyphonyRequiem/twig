# Query Command Closeout — Documentation Improvements

> **Status**: ✅ Done — All PRs merged | **Issue**: #1634 | **Parent Epic**: #1302
>
> **Note**: This plan resides on the `feature/1634-query-closeout` branch alongside pre-implementation source files. The delivered artifacts (XML doc comments, template section, instructions file) were merged to `main` via PRs #38 and #41 on separate branches — see `main` for the final state of all modified files.

## Executive Summary

This plan addresses three closeout findings from the Query Command epic (#1302) — correcting misleading XML doc comments on `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` to document the push-on-write vs stage-and-batch dual-path model; adding a structured close-out checklist section to the plan template for consistent wrap-up across future epics; and creating a centralized Copilot instruction file with the "2-PR sweet spot, 3-PR threshold" PR grouping heuristic. All deliverables are documentation and process improvements with no runtime behavior changes, delivered across two PRs on separate branches (see [Alternatives Considered](#alternatives-considered)) and merged to `main`.

---

## Background

The three closeout findings each address a different documentation gap in the existing codebase: misleading XML doc comments in the note flushing subsystem, a missing close-out section in the plan template, and undocumented PR grouping strategy. Issue #1634 is the parent work item for this closeout effort, living under Epic #1302 (Query Command) with three child Tasks in ADO: #1635 (note flushing semantics), #1636 (close-out checklist template), and #1637 (PR grouping guidance). Each child Task maps to one Issue section in this plan.

### Current State

#### Note Flushing Architecture

The twig CLI underwent a major architectural transformation in Epic #1338 (Push-on-Write and Sync Convergence), which moved `NoteCommand` and `EditCommand` from a stage-then-save model to push-on-write with automatic cache resync. The current implementation of `twig note` follows a hybrid pattern:

- **Non-seed items**: Push-on-write via `AddCommentAsync()` with offline fallback to local staging
- **Seed items**: Always stage locally (seeds have no ADO identity to push to)
- **Auto-flush**: `update`, `state`, and `edit` commands auto-push any pending notes via `AutoPushNotesHelper.PushAndClearAsync()`
- **Batch flush**: `PendingChangeFlusher.FlushAsync()` handles notes during `twig save`, `twig sync`, and `twig flow-done`

While the push-on-write convergence resolved the primary inconsistency (notes no longer require explicit `twig save`), the code lacks clear documentation of the canonical behavior. Three distinct flush paths exist, and the relationship between them is implicit rather than documented. Compounding this, `NoteCommand`'s existing XML doc summary is **factually incorrect** post-push-on-write — for non-seed items, notes are pushed immediately via `AddCommentAsync()`, not stored as pending. The verbatim incorrect summary:

> `"Implements <c>twig note ["text"]</c>: adds a note to the active work item. If text is provided inline, stores as pending. Otherwise launches editor."`

#### Plan Template System

The project uses `.plan.md` documents in `docs/projects/` for feature planning. A formal template exists at `.github/templates/implementation-plan.md` — but it lacks a close-out/completion section. In practice, several completed plans have organically developed inline completion annotations using bold text markers like `**Completion Notes**` and `**Completion Date**` within task or epic sections, but their format varies and the pattern is not codified in the template.

#### PR Grouping Documentation

PR grouping guidance is distributed across individual plan documents (9 completed plans contain PR group sections, excluding this plan) but no central reference document exists. Plans use `## PR Groups` sections with Deep/Wide classification and sizing guardrails (≤2000 LoC, ≤50 files), but the strategic insight about optimal PR count per epic is undocumented. The `.github/copilot-instructions.md` file does not currently reference PR groups — the guidance exists only in per-plan sections and in the system prompts provided to planning agents at runtime.

---

## Problem Statement

1. **Misleading note flushing documentation**: The note flushing subsystem spans three code paths and three flush mechanisms across `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` (see *Background § Note Flushing Architecture* for the full architecture). The canonical behavior is correct, but the existing documentation is **actively misleading** — `NoteCommand`'s XML doc summary claims notes are "stored as pending" when non-seed items are pushed immediately via `AddCommentAsync()`. Beyond the incorrect summary, there are no `<remarks>` blocks explaining *why* the hybrid pattern exists or *when* each path activates. A developer encountering the code for the first time would be misled by the existing docs and could not determine the intended semantics without reading all three components together.

2. **No close-out checklist in plan template**: The formal template at `.github/templates/implementation-plan.md` ends at "Notes" with no close-out guidance. While six completed plans have developed ad-hoc completion annotations organically (see *Background § Plan Template System*), the format varies and none use a dedicated section heading. Without a template section, epic close-out depends on tribal knowledge — agents and developers skip close-out steps or perform them inconsistently.

3. **Undocumented PR grouping strategy**: The "2-PR sweet spot, 3-PR threshold" insight exists only in verbal/session knowledge. Current plans use PR groups mechanically (Deep/Wide classification, LoC limits) but don't address the strategic question of *how many* PR groups an epic should target. This leads to plans that fragment work into 4+ PRs unnecessarily, increasing review overhead.

## Goals and Non-Goals

### Goals

| ID | Goal | Tasks |
|----|------|-------|
| G-1 | Add comprehensive XML doc comments to `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` documenting the canonical note flushing semantics | T-1635-1, T-1635-2, T-1635-3 |
| G-2 | Add a `## Close-Out Checklist` section to `.github/templates/implementation-plan.md` | T-1636-1 |
| G-3 | Create `.github/instructions/pr-grouping.instructions.md` with PR grouping strategy guidance | T-1637-1 |
| G-4 | Update `.github/copilot-instructions.md` to reference the new PR grouping instructions — currently the file contains zero PR group references, so this introduces the cross-link for the first time | T-1637-2 |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG-1 | Refactoring the note flushing implementation — the current hybrid pattern is intentional and correct; only documentation is needed |
| NG-2 | Changing the `AutoPushNotesHelper` error handling gradient — `EditCommand` catches both auto-push and resync failures, `StateCommand` propagates auto-push but catches resync, and `UpdateCommand` propagates both. This plan documents the existing gradient in XML doc comments but does not modify the error-handling behavior itself. |

## Requirements

### Functional Requirements

| ID | Requirement | Tasks |
|----|-------------|-------|
| FR-1 | Replace the factually incorrect XML doc summary on `NoteCommand` and add `<remarks>` documenting the three code paths (push-on-write, offline fallback, seed staging) and inter-component relationships | T-1635-1 |
| FR-2 | Document `AutoPushNotesHelper`'s role as a side-effect flusher with `<summary>` and `<remarks>`, including the error-handling gradient across its three call sites (`EditCommand`, `StateCommand`, `UpdateCommand`) | T-1635-2 |
| FR-3 | Add note-specific `<remarks>` to `IPendingChangeFlusher.FlushAsync` clarifying the batch flush fallback path; use `<inheritdoc/>` on `PendingChangeFlusher.FlushAsync`; update `FlushAsync` summary to mention notes explicitly | T-1635-3 |
| FR-4 | Create `.github/instructions/pr-grouping.instructions.md` with the "2-PR sweet spot, 3-PR threshold" heuristic and cross-link it from `.github/copilot-instructions.md` | T-1637-1, T-1637-2 |
| FR-5 | Add a `## Close-Out Checklist` section to `.github/templates/implementation-plan.md` formalizing the close-out pattern observed in completed plans | T-1636-1 |

### Non-Functional Requirements

| ID | Requirement | Tasks |
|----|-------------|-------|
| NFR-1 | All XML doc comments must compile cleanly under `TreatWarningsAsErrors=true` — including `<see cref=""/>` references to private members (e.g., `StageLocallyAsync`) resolving without CS1574 warnings | T-1635-1, T-1635-2, T-1635-3 |
| NFR-2 | PR grouping guidance must be advisory, not prescriptive — plans may deviate with documented rationale | T-1637-1 |

## Proposed Design

### Architecture / Approach Overview

This is a documentation-only effort — no runtime behavior changes. The approach is to add XML doc comments to three existing components (`NoteCommand`, `AutoPushNotesHelper`, `PendingChangeFlusher`) that together implement the note flushing subsystem, update the plan template with a formalized close-out checklist section, and create a centralized PR grouping strategy document cross-linked from `.github/copilot-instructions.md`. Each deliverable is independently committable and testable. The XML doc changes must compile cleanly under `TreatWarningsAsErrors=true`; the template and instructions changes are pure Markdown with no build validation. Note that `StageLocallyAsync()` is `private` in `NoteCommand`, but because XML doc `<see cref=""/>` references resolve within the same type, the docs use `<see cref="StageLocallyAsync"/>` to cross-reference the method.

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | **Close-Out Checklist placement**: append after `## Notes` at the end of the plan template | Matches the organic convention established by completed plans, which placed inline `**Completion Notes**` / `**Completion Date**` annotations at the end of their task and epic blocks. The new `## Close-Out Checklist` heading formalizes and elevates this pattern into a dedicated, discoverable section. Preserves natural chronological flow — planning sections first, post-completion record last. |

### Testing Strategy

No new tests are required. All changes are documentation-only:

- **XML doc comments**: Validated by `dotnet build` under `TreatWarningsAsErrors=true`, which catches malformed XML via CS1570/CS1571 warnings
- **Markdown template and instruction files**: No build validation — reviewed manually for formatting and content accuracy
- **Regression coverage**: Existing tests in `AutoPushNotesHelperTests.cs` and `PendingChangeFlusherTests.cs` cover the documented components' runtime behavior

---

## Alternatives Considered

### Branch Strategy: Single Feature Branch vs Two Separate Branches

The original plan assumed a single long-lived feature branch (`feature/1634-query-closeout`) for all three Issues. During implementation, the feature branch diverged significantly from `main` — 3 commits ahead but `main` had advanced past it — making it unsuitable for clean PRs.

| Approach | Pros | Cons |
|----------|------|------|
| **Single feature branch** | Simpler branch management; single integration point | Higher merge-conflict risk from divergence; all-or-nothing merge blocks independent review |
| **Two separate branches** *(chosen)* | Each branch is short-lived and merges independently; eliminates cross-PR conflict; enables independent review timelines | Slightly more branch management overhead; requires coordinating two merges |

The two-branch approach was adopted in response to observed divergence. Each branch mapped to one PR group (PG-1 → `users/dangreen/pg1-note-flushing-docs-closeout-template` → PR #38, PG-2 → `feature/pg2-pr-grouping-instructions` → PR #41), both created from `main` and merged independently. The original `feature/1634-query-closeout` branch was not used for any merged implementation. This aligned with the PR grouping guidance being documented in this very plan (the "2-PR sweet spot" heuristic).

### PR Count: Two-PR vs Three-PR Delivery

The three Issues (#1635, #1636, #1637) could plausibly have been delivered as three separate PRs — one per Issue — for maximum independence.

| Approach | Pros | Cons |
|----------|------|------|
| **Three PRs** (one per Issue) | Maximum independence; each Issue reviewed in isolation; smallest possible diff per PR | Increased review overhead (3 review cycles instead of 2); over-fragments what is already a small effort (~270 LoC total); contradicts the "2-PR sweet spot" guidance being documented in this very plan |
| **Two PRs** *(chosen)* | Balances independence with review efficiency; groups the mechanical documentation changes (PG-1) separately from the strategic guidance (PG-2); demonstrates the heuristic being documented | PG-1 spans two Issues, so a reviewer must mentally track both #1635 and #1636 in one review |

Two PRs were chosen because the combined scope is small enough that grouping #1635 and #1636 (both documentation-only, no code overlap) imposes minimal cognitive overhead, while splitting them into three PRs would add a full review cycle for negligible reviewability gain.

---

## Dependencies

No external dependencies. Internal sequencing constraints are minimal:

### Internal Dependencies

- **Push-on-write convergence (Epic #1338)**: The note flushing documentation (Issue #1635) depends on the push-on-write architecture being stable and complete. This prerequisite was met prior to this plan's execution — Epic #1338 was completed and merged to `main` before work began.
- **Plan template**: The close-out checklist (Issue #1636) appends to `.github/templates/implementation-plan.md`, which must exist. No concurrent modifications to this file were in flight.
- **Copilot instructions**: The PR grouping cross-link (T-1637-2) modifies `.github/copilot-instructions.md`. Concurrent edits to this file could create merge conflicts, mitigated by the short-lived branch strategy (see [Alternatives Considered](#alternatives-considered)).

### Sequencing Constraints

The three Issues are independent — no sequencing constraints between them. Within Issue #1637, T-1637-1 (create the instructions file) must precede T-1637-2 (cross-link from `copilot-instructions.md`) so the link target exists.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation | Outcome |
|------|-----------|--------|------------|---------|
| XML doc `<remarks>` and `<list>` elements produce CS1570/CS1571 warnings, breaking the build under `TreatWarningsAsErrors=true` | Medium | High | Validate XML doc syntax by running `dotnet build` after each T-1635 task. Use only well-formed XML elements (`<para>`, `<list>`, `<item>`, `<c>`) and avoid bare angle brackets or unescaped ampersands in doc comments. Review compiler output for CS1570/CS1571 specifically before committing. | Did not materialize — all XML docs compiled cleanly. |
| Feature branch diverges significantly from `main` — merge conflicts possible when creating PRs | Medium | Medium | Rebase or merge `main` into the feature branch before creating PRs. Point-in-time divergence snapshots (e.g., commit counts) age quickly; the mitigation is structural rather than time-dependent. | Mitigated by delivering work on two separate short-lived branches (see [Alternatives Considered](#alternatives-considered)) rather than a single long-lived feature branch. Both merged cleanly to `main` as PRs #38 and #41. The original `feature/1634-query-closeout` branch was not used for any merged implementation and remains diverged from `main`. |

---

## Open Questions

None — all design decisions were resolved during implementation. The three deliverables (XML doc comments, template section, instructions file) were straightforward documentation tasks with no ambiguous requirements or unresolved trade-offs.

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

**T-1635-3 Details**:

- **Existing coverage preserved**: `PendingChangeFlusher` already has substantial class-level `<remarks>` covering continue-on-failure semantics, notes-only conflict bypass, and cache resync. `IPendingChangeFlusher` already documents callers (`SaveCommand`, `SyncCommand`, `FlowDoneCommand`) in its class-level `<summary>` — this caller list predates this task and is preserved, not newly added.
- **New additions**: A `<remarks>` note on `IPendingChangeFlusher.FlushAsync` clarifying that the note batch flush path is the fallback for residual staged notes (from offline fallback or seed publishing), and an updated `FlushAsync` summary mentioning notes explicitly.
- **Inheritance pattern**: `PendingChangeFlusher.FlushAsync` uses `<inheritdoc/>` to inherit the interface's documentation — the correct pattern for concrete implementations, avoiding doc comment drift between interface and class.

**Acceptance Criteria** *(all met — delivered in PR #38)*:
- [x] All XML doc comments compile cleanly under `TreatWarningsAsErrors=true` — including `<see cref="StageLocallyAsync"/>` resolving without CS1574 warnings (the method is `private` in `NoteCommand`, but `<see cref=""/>` resolves within the same type)
- [x] The incorrect `"stores as pending"` summary on `NoteCommand` is replaced with accurate push-on-write documentation (see *Background § Note Flushing Architecture* for the verbatim incorrect text)
- [x] A developer reading `NoteCommand.cs` can determine the canonical flushing model without reading other files
- [x] The relationship between `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher` is documented in each component's XML docs
- [x] `FlowDoneCommand` remains listed as a caller in `IPendingChangeFlusher`'s class-level `<summary>` (pre-existing content preserved, not a new addition)
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

**Acceptance Criteria** *(all met — delivered in PR #38)*:
- [x] Template contains a `## Close-Out Checklist` section with all required subsections
- [x] The checklist includes ADO state transition steps (`twig state Done` for tasks, issues, and parent epic)
- [x] The checklist includes PR reference fields (PR number, title, status)
- [x] The format is consistent with the organic completion annotation pattern established by six completed plans (inline `**Completion Notes**` / `**Completion Date**` bold text), now elevated to a dedicated section heading

---

### Issue #1637: Document PR grouping guidance (2-PR sweet spot, 3-PR threshold)

**Goal**: Create a centralized PR grouping strategy document and integrate it into the Copilot agent instruction set. *(Supports G-3, G-4)*

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1637-1 | Create `.github/instructions/pr-grouping.instructions.md` with PR grouping strategy content (see Details below) | `.github/instructions/pr-grouping.instructions.md` | Medium |
| T-1637-2 | Add PR grouping reference to `.github/copilot-instructions.md`; fix dangling `process-agnostic.instructions.md` reference (see Details below) | `.github/copilot-instructions.md` | Small |

**T-1637-1 Details**: Create the centralized PR grouping instructions file with the following content areas:

- **PR group definition** — a cross-cutting overlay for code review, not a 1:1 mapping to the ADO hierarchy
- **Sizing guardrails** — ≤2000 LoC, ≤50 files per group
- **Deep vs Wide classification** — criteria for each type, review implications, and concrete examples from Twig plans
- **Optimal count guidance** — the "2-PR sweet spot, 3-PR threshold" heuristic with rationale, explicitly noting that this derives from observational patterns across Twig's completed epics (a limited, single-project sample without controlled comparison) and should be treated as advisory, not prescriptive — no formal metrics (review turnaround, defect rate, rework frequency) were collected to validate the thresholds
- **PG-N naming convention** — why PG-N over PR-N
- **Anti-patterns** — over-fragmentation (4+ PRs increasing context-switching overhead) and mega-PRs (single PR for entire epic reducing reviewability)

**T-1637-2 Details**:

1. **Add `### PR Grouping Strategy` subsection** to `.github/copilot-instructions.md` inside the existing `## ADO Work Item Tracking` section, placed after `### Commit conventions` (which ends with *"If no mapping exists for the current work, commit normally without AB# reference"*) and before `## Work Item Lifecycle Protocol`
2. **Content**: Brief description of PR grouping purpose plus a `See .github/instructions/pr-grouping.instructions.md for the full guide` reference
3. **Fix dangling file reference**: Replace the line *"See `.github/instructions/process-agnostic.instructions.md` for the full principle."* in the `## Project Overview` section with inline text that makes the bullet self-contained — specifically: *"Never assume a specific process template (Agile, Scrum, CMMI, Basic) — discover states, types, and fields dynamically via the provider."* — removing the dependency on a non-existent file

**Acceptance Criteria** *(all met — delivered in PR #41)*:
- [x] A new `.github/instructions/pr-grouping.instructions.md` file exists with all required sections
- [x] The document includes the "2-PR sweet spot, 3-PR threshold" finding with concrete rationale and a sample-size caveat
- [x] Deep vs Wide classification criteria are clearly defined with examples
- [x] `.github/copilot-instructions.md` contains a `### PR Grouping Strategy` subsection under `## ADO Work Item Tracking`, between `### Commit conventions` and `## Work Item Lifecycle Protocol`
- [x] The guidance is advisory, not prescriptive — allows deviation with rationale
- [x] The dangling `.github/instructions/process-agnostic.instructions.md` reference in `copilot-instructions.md` is replaced with inline text

---

## PR Groups

This closeout effort is delivered as two PR groups, separating the code-touching XML doc changes and template update (PG-1) from the pure-Markdown strategy documentation (PG-2). Each is classified as **deep** (few files, complex or nuanced changes requiring careful review) or **wide** (many files, mechanical or formulaic changes that can be reviewed quickly) — see the individual tables below for classification rationale.

### PG-1: Note Flushing Documentation + Close-Out Template

| Attribute | Value |
|-----------|-------|
| **Issues** | #1635, #1636 |
| **Tasks** | T-1635-1, T-1635-2, T-1635-3, T-1636-1 |
| **Classification** | Wide |
| **Estimated LoC** | ~120 (XML doc comments + template section) |
| **Files** | `src/Twig/Commands/NoteCommand.cs`, `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs`, `src/Twig/Commands/PendingChangeFlusher.cs`, `src/Twig/Commands/IPendingChangeFlusher.cs`, `.github/templates/implementation-plan.md` |
| **Rationale** | Both Issues #1635 and #1636 are documentation-only changes that share no code dependencies. Grouping them reduces PR overhead while keeping the review focused on documentation quality — 5 files with formulaic additions fit comfortably within the ≤50 file guardrail. |

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

### Completion Date

**Implementation Completed**: 2026-04-16 | **PRs Merged**: 2026-04-16

### Pull Requests

| PR | Title | Branch | PR Group | Status |
|----|-------|--------|----------|--------|
| #38 | Note flushing documentation + close-out template | `users/dangreen/pg1-note-flushing-docs-closeout-template` | PG-1 | ✅ Merged (2500bad) |
| #41 | PR grouping instructions | `feature/pg2-pr-grouping-instructions` | PG-2 | ✅ Merged (2e9073c) |

### Completion Summary

All three Issues delivered documentation and process improvements with no runtime code changes. Issue #1635 corrected the factually incorrect `NoteCommand` XML doc summary and added comprehensive `<remarks>` blocks to `NoteCommand`, `AutoPushNotesHelper`, and `PendingChangeFlusher`, documenting the push-on-write vs stage-and-batch semantics and the error-handling gradient across call sites. Issue #1636 added a structured `## Close-Out Checklist` section to the plan template at `.github/templates/implementation-plan.md`, formalizing the close-out pattern observed in 6 completed plans. Issue #1637 created `.github/instructions/pr-grouping.instructions.md` with the "2-PR sweet spot, 3-PR threshold" heuristic and cross-linked it from `.github/copilot-instructions.md`.

**Branch note**: Implementation was delivered across two separate branches rather than the originally planned single `feature/1634-query-closeout` branch — PG-1 was implemented on `users/dangreen/pg1-note-flushing-docs-closeout-template` and PG-2 on `feature/pg2-pr-grouping-instructions`. Both branches were merged to `main` independently (see [Alternatives Considered](#alternatives-considered)).

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

