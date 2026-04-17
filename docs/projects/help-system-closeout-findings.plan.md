# Help System (Epic 1522) Closeout Findings

> **Status**: 🔨 In Progress
> **Work Item**: [#1668](https://dev.azure.com/) — Help System (Epic 1522) Closeout Findings
> **Parent Epic**: #1603 — Follow Up on Closeout Findings
> **Revision**: 0 (initial draft)

---

## Executive Summary

This plan addresses four improvement findings identified during the close-out review of the Help System epic (#1522). The findings target process improvements to the SDLC workflow rather than the twig CLI product code itself: (1) automated validation that keeps the `CommandExamples.cs` example registry in sync with registered commands, (2) standardized worktree naming conventions so the close-out agent reliably discovers feature branches across worktree layouts, (3) a diff summary generation step in the close-out prompt that condenses mechanical changes spanning many commands into grouped summaries, and (4) a summary-only close-out appendix format that reduces plan document bloat. The work spans C# tests (one Issue), prompt engineering (two Issues), and document template changes (one Issue), estimated at ~310 LoC total.

## Background

### Current Architecture

The twig CLI help system consists of three interlocking components:

| Component | File | Role |
|-----------|------|------|
| `GroupedHelp` | `src/Twig/Program.cs` (lines 866–1087) | Organizes commands into 8 semantic categories for the root `--help` output |
| `CommandExamples` | `src/Twig/CommandExamples.cs` | Static dictionary of ~50 commands → example lines, appended to per-command `--help` |
| `HintEngine` | `src/Twig/Hints/HintEngine.cs` | Post-execution contextual hints (e.g., "Try: twig status") |

The SDLC close-out agent is Phase 5 of the `twig-sdlc` Conductor workflow, defined in:

| File | Role |
|------|------|
| `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | 11-step agent prompt (Steps 0–10) |
| `.github/skills/twig-sdlc/assets/prompts/close-out.system.md` | Agent identity and verification rules |
| `.github/skills/twig-sdlc/assets/twig-sdlc.yaml` (lines 790–825) | Agent definition, routing, output schema |

The close-out agent produces `observations`, `improvements`, and `agent_struggles` outputs. These are consumed by the `closeout-filing` workflow (`.github/skills/closeout-filing/`) which files them as ADO Issues/Tasks under Epic #1603.

### Context

During the Help System epic close-out, four process improvement observations were filed as Issue #1668 with child Tasks #1669–#1672. These are not product bugs — they are workflow/tooling improvements that reduce friction in future SDLC runs:

1. **Example drift risk** — `CommandExamples.cs` is manually maintained with no automated check that example keys match registered commands or that all commands have examples. Drift was observed during the Help System epic when new commands were added without corresponding examples.

2. **Worktree branch discovery** — The close-out agent's Step 1b runs `git branch --no-merged main` to find orphaned branches. In a linked worktree, this may not list branches created in the main worktree. No convention exists for worktree naming that the agent can rely on.

3. **Mechanical change noise** — When an epic makes the same change across many commands (e.g., adding `--output json` to every formatter), the close-out agent's git log analysis (Step 5) and meta-observations (Step 6) produce verbose, repetitive analysis. A grouping/summarization step would reduce noise.

4. **Plan document bloat** — Plan documents average 40–70 KB. The close-out agent's Step 4 adds a completion section, and observations are filed separately. A summary-only appendix format would keep the plan focused while preserving an audit trail.

### Prior Art

- `streamline-closeout-fast-path.plan.md` — Prompt-only optimization to the close-out agent (Step 0b fast-path). Demonstrates the pattern of modifying `close-out.prompt.md` without C# code changes.
- `child-state-verification-gate.plan.md` — Task #1621 added worktree-aware branch cleanup to `FlowCloseCommand.cs`. The worktree detection pattern (`GetWorktreeRootAsync()` comparing `git-common-dir` vs `git-dir`) is already proven in the codebase.
- `CommandExamples.ShowIfPresent()` is called from `Program.cs:134` after `app.Run(args)` when `--help` is requested.
- `GroupedHelp.KnownCommands` (Program.cs lines 874–975) is a manually-maintained `HashSet<string>` of all CLI verb names — the canonical registry for validation.

### Call-Site Audit

The changes in this plan do not modify cross-cutting C# code. Task #1669 adds new test files only. Tasks #1670–#1672 modify prompt/documentation files. No call-site audit is required.

## Problem Statement

The SDLC close-out workflow has four friction points identified during the Help System epic (#1522) close-out:

1. **No automated validation of command examples.** The `CommandExamples.cs` dictionary contains ~50 manually-maintained entries. There is no test that verifies (a) every example key corresponds to a registered command, (b) every registered command has at least one example, or (c) the example format is consistent (starts with `twig`, uses correct verb). When commands are added, renamed, or removed, examples can silently drift. The `GroupedHelp.KnownCommands` HashSet provides the canonical command registry to validate against, but no such validation exists today.

2. **No worktree naming convention for close-out branch discovery.** The close-out agent's Step 1b uses `git branch --no-merged main` to find orphaned feature branches. When SDLC runs use linked Git worktrees (a common pattern for parallel work), branches created or checked out in one worktree may not be visible from another. The codebase has worktree detection (`GetWorktreeRootAsync()`), but no documented convention for worktree naming or layout that the close-out agent can rely on. This can cause false negatives (missing branches) or false positives (flagging branches from unrelated worktrees).

3. **Verbose analysis of mechanical changes.** When an epic applies the same change pattern across many commands (e.g., adding `--output json` support to 20 commands), the close-out agent's Step 5 git log and Step 6 meta-observations treat each command as a separate change, producing repetitive analysis. There is no instruction to group similar changes or produce a summarized diff description. This wastes LLM tokens and obscures more interesting observations.

4. **Plan documents grow indefinitely.** Plan documents in `docs/projects/` average 40–70 KB. The close-out agent's Step 4 adds a completion section. Observations are separately filed under Epic #1603. There is no mechanism to append a concise summary (instead of full detail) to the plan, keeping the primary document focused while preserving an audit trail.

## Goals and Non-Goals

### Goals

1. **G-1: Command example coverage guarantee** — A failing test when any registered command lacks examples, or any example key references a non-existent command
2. **G-2: Worktree-safe branch discovery** — Documented conventions and prompt logic that correctly enumerate feature branches regardless of worktree layout
3. **G-3: Mechanical change summarization** — Close-out observations group repeated patterns instead of enumerating each instance
4. **G-4: Compact close-out appendix** — Plan documents receive a ≤20-line summary appendix instead of verbose completion detail

### Non-Goals

- **NG-1: Runtime CLI validation** — We are not adding a `twig validate-examples` command; validation is test-time only
- **NG-2: Worktree lifecycle management** — We are not automating worktree creation/deletion; only standardizing naming conventions
- **NG-3: Automated diff tooling** — The diff summary is LLM-produced via prompt instruction, not a deterministic script
- **NG-4: Retroactive plan trimming** — Existing plan documents will not be modified; the appendix format applies to future close-outs only
- **NG-5: C# code changes beyond tests** — Tasks #1670–#1672 are prompt/documentation changes only; no production C# code is modified

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR1 | A test validates that every example line in `CommandExamples.Examples` starts with `"twig "` and contains the command verb |
| FR2 | A test validates that every example line has descriptive text after the command invocation |
| FR3 | Existing bidirectional sync tests (already in `GroupedHelpTests.AllCommands_HaveExamples`) continue to pass |
| FR4 | A worktree naming convention is documented in `.github/instructions/` |
| FR5 | The close-out prompt Step 1b includes worktree-aware branch enumeration using `git worktree list` |
| FR6 | The close-out prompt includes a diff summarization instruction between Steps 5 and 6 |
| FR7 | The close-out prompt Step 4 emits a summary-only appendix section (≤20 lines) to the plan document |
| FR8 | The architect prompt template includes an optional `## Close-Out Summary` appendix placeholder |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR1 | All test changes must be AOT-compatible (no reflection, use static references) |
| NFR2 | Prompt changes must be backward-compatible — no YAML schema changes to `twig-sdlc.yaml` |
| NFR3 | Convention documentation must be self-contained (no cross-references to external systems) |
| NFR4 | Tests must pass in CI (`dotnet test --settings test.runsettings`) |

## Proposed Design

### Architecture Overview

This plan modifies four layers of the system, all additive:

```
┌─────────────────────────────────────────────────────┐
│  C# Test Layer (Issue #1669)                        │
│  └─ GroupedHelpTests.cs (extended)                   │
│     Adds format validation to existing sync tests   │
├─────────────────────────────────────────────────────┤
│  Prompt Layer (Issues #1670, #1671, #1672)           │
│  └─ close-out.prompt.md (modified)                   │
│     ├─ Step 1b: worktree-aware branch discovery      │
│     ├─ Step 5b: mechanical diff summarization (new)  │
│     └─ Step 4: summary appendix format               │
├─────────────────────────────────────────────────────┤
│  Convention Layer (Issue #1670)                       │
│  └─ worktree-conventions.instructions.md (new)       │
│     Documents naming, layout, discovery patterns     │
├─────────────────────────────────────────────────────┤
│  Template Layer (Issue #1672)                        │
│  └─ architect.prompt.md (modified)                   │
│     Adds Close-Out Summary appendix placeholder      │
└─────────────────────────────────────────────────────┘
```

### Key Components

#### Component 1: Command Example Validation Tests

**File:** `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` (extended)

**Existing coverage (in `GroupedHelpTests.cs`):**
- `AllCommands_HaveExamples` — validates bidirectional sync (non-hidden commands ↔ example keys), ≥2 examples per command, no blank lines, no orphaned entries
- `AllNonHiddenCommands_AppearInGroupedHelp` — validates commands in `KnownCommands` appear in help output

**Gaps to fill (new tests in the same file):**
1. `AllExampleLines_StartWithTwigPrefix` — every example string starts with `"twig "`
2. `AllExampleLines_ContainCommandVerb` — each example line contains the command verb from its dictionary key (e.g., key `"set"` → examples contain `"set"`)
3. `AllExampleLines_HaveDescriptionSuffix` — each example line has descriptive text after the command invocation (prevents stub entries)

**Design:**
- Added to existing `GroupedHelpTests.cs` (not a new file) since the existing tests already validate command↔example sync
- Uses xUnit `[Fact]` with Shouldly assertions
- Accesses `CommandExamples.Examples` directly (already referenced in the file)
- AOT-safe — no reflection needed for example validation

#### Component 2: Worktree-Aware Branch Discovery

**Files:**
- `.github/instructions/worktree-conventions.instructions.md` (new)
- `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` (modified Step 1b)

**Convention document contents:**
- Standard worktree directory layout: `../twig2-wt-<branch-slug>/` (sibling to main repo)
- Naming pattern: `<repo>-wt-<branch-slug>` where `<branch-slug>` is the PR group branch name
- Discovery command: `git worktree list --porcelain` from any worktree
- Agent instructions: always run `git worktree list` before `git branch --no-merged main` to enumerate all worktrees and their checked-out branches

**Prompt change (Step 1b):**
- Prepend `git worktree list --porcelain` to discover all worktrees and their HEAD branches
- Cross-reference worktree branches with `git branch --no-merged main`
- Union the results to ensure no branches are missed due to worktree isolation

#### Component 3: Mechanical Diff Summarization

**File:** `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` (new Step 5b)

**Design:**
A new Step 5b is inserted between Step 5 (git log) and Step 6 (meta-observations):

1. After collecting the full git log (Step 5), analyze the commit messages and diffs
2. Identify **mechanical patterns**: commits that apply the same change to multiple files/commands (e.g., "add --output json to X command" repeated 15 times)
3. Group mechanical changes into a single summary line: "Added `--output json` flag to 15 commands (set, show, status, ...)"
4. Carry the grouped summary into Step 6 instead of the raw per-commit list
5. Non-mechanical changes (unique logic, bug fixes, design decisions) remain itemized

**Heuristics for mechanical change detection:**
- Same commit message pattern with only the target name varying
- Same diff shape (e.g., adding the same parameter to different methods)
- ≥3 commits with the same pattern qualifies as "mechanical"

#### Component 4: Summary-Only Close-Out Appendix

**Files:**
- `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` (modified Step 4)
- `.github/skills/twig-sdlc/assets/prompts/architect.prompt.md` (add appendix placeholder)

**Appendix format (≤20 lines):**
```markdown
## Close-Out Summary

> **Completed**: <date>
> **Duration**: <first commit> → <last commit> (<N> days)
> **Commits**: <N> commits across <N> PR groups
> **Observations**: See Issue #<closeout_issue_id> under Epic #1603

### Key Outcomes
- <1-sentence summary of what was delivered>
- <1-sentence summary of notable process observations>
- <N> improvement suggestions filed → Issue #<closeout_issue_id>
```

**Step 4 modification:**
- Replace the current verbose completion section with the compact appendix format
- The full observations remain in the ADO work item (Step 7) and the closeout-filing Issue
- The plan document gets only the concise summary

### Design Decisions

#### D1: Test-time validation vs. runtime validation

**Decision:** Validate command examples at test time only, not at runtime.

**Rationale:** Runtime validation would add startup cost and is unnecessary — examples are static data compiled into the binary. Test-time validation catches drift in CI before release. This aligns with the existing pattern where `GroupedHelp.KnownCommands` is also only validated implicitly through usage.

#### D2: Worktree naming as convention, not enforcement

**Decision:** Document worktree naming as a convention in `.github/instructions/`, not enforce it programmatically.

**Rationale:** Worktree creation is an interactive developer/agent action outside twig's control. The convention must be discoverable by agents (via instruction files loaded into context) but cannot be enforced by the CLI. The `git worktree list` command provides a reliable discovery mechanism regardless of naming.

#### D3: LLM-based diff summarization vs. deterministic script

**Decision:** Use prompt instructions for diff summarization, not a deterministic script.

**Rationale:** Mechanical change detection requires semantic understanding of commit messages and diff patterns. A deterministic script would need complex heuristics that are fragile and hard to maintain. The close-out agent (running on Opus 1M) has the context and reasoning capacity to identify and group patterns. This follows the existing approach where the close-out agent already performs meta-analysis in Step 6.

#### D4: Appendix in plan document vs. separate file

**Decision:** Add the close-out summary as an appendix section within the existing plan document.

**Rationale:** A separate file would fragment the audit trail. An appendix section keeps the summary co-located with the plan while staying out of the way of the main content. This follows the pattern seen in `twig-vscode-extension.plan.md` ("Appendix: Resolved Design Decisions") and `twig-architecture-analysis.doc.md` ("Appendix — Metrics and References").

## Dependencies

### External Dependencies
- None — all changes use existing tools and frameworks

### Internal Dependencies
- `GroupedHelp.KnownCommands` (Program.cs) — must be accessible from test project for validation
- `CommandExamples.Examples` (CommandExamples.cs) — must be accessible from test project
- Both are `internal` with `InternalsVisibleTo` already configured for `Twig.Cli.Tests`

### Sequencing Constraints
- Issue #1669 (validation tests) has no dependencies on other Issues
- Issue #1670 (worktree conventions) has no dependencies
- Issue #1671 (diff summary) depends on understanding of Step 5 output, but no code dependency
- Issue #1672 (appendix) should be implemented after #1671 since both modify close-out.prompt.md — but they modify different steps and can be merged independently

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should hidden/aliased commands (e.g., backward-compat aliases marked `[Hidden]`) be excluded from the "all commands have examples" validation? | Low | Proposed: yes, exclude hidden commands. The test can use a static exclusion set. |
| OQ-2 | Should the worktree naming convention be enforced by `twig flow-start` (auto-create worktree with standard name)? | Low | Out of scope for this plan (see NG-2). Could be a future enhancement. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `.github/instructions/worktree-conventions.instructions.md` | Documents worktree naming and discovery conventions |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` | Add three format validation tests for command example lines |
| `src/Twig/CommandExamples.cs` | Fix any example format gaps surfaced by new tests |
| `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | Step 1b: worktree-aware branch discovery; Step 4: summary appendix format; new Step 5b: diff summarization |
| `.github/skills/twig-sdlc/assets/prompts/architect.prompt.md` | Add optional Close-Out Summary appendix placeholder in plan template |

## ADO Work Item Structure

**Input:** Issue #1668 — Help System (Epic 1522) Closeout Findings
**Parent:** Epic #1603 — Follow Up on Closeout Findings

All four child Issues already exist in ADO. Tasks are defined below for each.

---

### Issue #1669: Automated validation of generated command examples against CLI output

**Goal:** Ensure `CommandExamples.cs` stays in sync with registered CLI commands through automated test validation.

**Prerequisites:** None

**Existing coverage:** `GroupedHelpTests.AllCommands_HaveExamples` already validates bidirectional sync (non-hidden commands ↔ example keys), ≥2 examples per command, no blank lines, and no orphaned entries. The gap is **format validation** of example line content.

**Tasks:**

| Task | Description | Files | Est. LoC | Status |
|------|-------------|-------|----------|--------|
| T1669-1 | Add three format validation tests to `GroupedHelpTests.cs`: (a) `AllExampleLines_StartWithTwigPrefix` — every example string starts with `"twig "`, (b) `AllExampleLines_ContainCommandVerb` — each line contains its dictionary key's verb, (c) `AllExampleLines_HaveDescriptionSuffix` — each line has descriptive text after whitespace padding. Use xUnit `[Fact]` with Shouldly. | `tests/Twig.Cli.Tests/Commands/GroupedHelpTests.cs` | ~50 | TO DO |
| T1669-2 | Fix any validation gaps surfaced by the new tests — update example lines that don't match the expected format, add missing verb references, ensure description text follows each example invocation. Run tests to confirm green. | `src/Twig/CommandExamples.cs` | ~20 | TO DO |

**Acceptance Criteria:**
- [ ] Three new format validation tests exist in `GroupedHelpTests.cs` and pass
- [ ] All example lines start with `"twig "` and contain the command verb
- [ ] All example lines include descriptive text after the command invocation
- [ ] `dotnet test --settings test.runsettings` passes with zero failures

---

### Issue #1670: Standardize worktree conventions for close-out agent branch discovery

**Goal:** Document worktree naming conventions and make the close-out agent's branch verification worktree-aware.

**Prerequisites:** None

**Tasks:**

| Task | Description | Files | Est. LoC | Status |
|------|-------------|-------|----------|--------|
| T1670-1 | Create `worktree-conventions.instructions.md` documenting: standard directory layout (`../repo-wt-<slug>/`), naming pattern, discovery command (`git worktree list --porcelain`), agent instructions for branch enumeration across worktrees. | `.github/instructions/worktree-conventions.instructions.md` | ~60 | TO DO |
| T1670-2 | Update close-out prompt Step 1b to prepend `git worktree list --porcelain` before `git branch --no-merged main`. Add instruction to union worktree HEAD branches with unmerged branches for complete coverage. Add a note explaining why this is needed. | `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | ~15 | TO DO |

**Acceptance Criteria:**
- [ ] Worktree conventions document exists at `.github/instructions/worktree-conventions.instructions.md`
- [ ] Document covers naming, layout, and discovery patterns
- [ ] Close-out prompt Step 1b includes `git worktree list` before branch enumeration
- [ ] Step 1b instructions explain how to union worktree branches with unmerged branches
- [ ] No YAML schema changes to `twig-sdlc.yaml`

---

### Issue #1671: Generate diff summary for mechanical changes spanning many commands

**Goal:** Reduce noise in close-out observations by grouping repetitive mechanical changes into summarized patterns.

**Prerequisites:** None

**Tasks:**

| Task | Description | Files | Est. LoC | Status |
|------|-------------|-------|----------|--------|
| T1671-1 | Insert new Step 5b in `close-out.prompt.md` between Step 5 (git log) and Step 6 (meta-observations). The step instructs the agent to: (a) scan the commit log for repeated patterns (≥3 commits with same change shape), (b) group them into summary lines, (c) carry grouped summaries into Step 6 instead of raw per-commit analysis. | `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | ~30 | TO DO |
| T1671-2 | Add mechanical-change detection heuristics to Step 5b: same commit message template with varying target names, same diff shape across files, threshold of ≥3 for grouping. Include example input/output to guide the LLM. | `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | ~25 | TO DO |

**Acceptance Criteria:**
- [ ] Step 5b exists between Step 5 and Step 6 in the close-out prompt
- [ ] Step 5b defines mechanical change detection heuristics with ≥3 threshold
- [ ] Step 5b includes an example showing raw commits → grouped summary
- [ ] Step 6 instructions reference the grouped summary from Step 5b
- [ ] No YAML schema changes to `twig-sdlc.yaml`

---

### Issue #1672: Add summary-only close-out appendix to reduce plan document size

**Goal:** Replace verbose close-out completion sections with a compact (≤20 line) summary appendix in plan documents.

**Prerequisites:** None (but coordinate with #1671 since both modify `close-out.prompt.md`)

**Tasks:**

| Task | Description | Files | Est. LoC | Status |
|------|-------------|-------|----------|--------|
| T1672-1 | Define the close-out appendix template format: completion date, duration, commit/PR counts, link to closeout Issue, 2-3 key outcome bullet points. Document as a markdown block in the close-out prompt. | `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | ~20 | TO DO |
| T1672-2 | Modify close-out prompt Step 4 to emit the compact appendix format instead of the current verbose completion section. Update the instruction to: change Status line, append the `## Close-Out Summary` section, and explicitly instruct NOT to add full observation text to the plan. | `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` | ~15 | TO DO |
| T1672-3 | Add an optional `## Close-Out Summary` appendix placeholder at the end of the plan template in `architect.prompt.md`, with a comment indicating it is populated by the close-out agent. | `.github/skills/twig-sdlc/assets/prompts/architect.prompt.md` | ~5 | TO DO |

**Acceptance Criteria:**
- [ ] Close-out appendix template is defined with all required fields (date, duration, counts, link, outcomes)
- [ ] Step 4 emits the compact appendix format (≤20 lines)
- [ ] Step 4 no longer emits verbose completion details in the plan document
- [ ] Full observations continue to be recorded in ADO (Step 7) and filed via closeout-filing
- [ ] Architect prompt template includes the `## Close-Out Summary` placeholder
- [ ] No YAML schema changes to `twig-sdlc.yaml`

## PR Groups

PR groups cluster tasks for reviewable pull requests, independent of the ADO hierarchy.

### PG-1: Command Example Validation Tests

**Type:** Deep (few files, test logic)
**Issues covered:** #1669 (all tasks)
**Tasks:** T1669-1, T1669-2
**Estimated LoC:** ~70
**Estimated files:** 2 (`GroupedHelpTests.cs`, `CommandExamples.cs`)
**Successor:** None (independent)

**Rationale:** This is a self-contained C# change — a new test file plus potential fixes to the example registry. It has no interaction with the prompt changes in PG-2 and can be reviewed independently. The reviewer needs C# and testing expertise.

### PG-2: Close-Out Workflow Improvements

**Type:** Wide (multiple prompt/doc files, mechanical changes)
**Issues covered:** #1670 (all tasks), #1671 (all tasks), #1672 (all tasks)
**Tasks:** T1670-1, T1670-2, T1671-1, T1671-2, T1672-1, T1672-2, T1672-3
**Estimated LoC:** ~170
**Estimated files:** 3 (`close-out.prompt.md`, `worktree-conventions.instructions.md`, `architect.prompt.md`)
**Successor:** None (independent)

**Rationale:** All three Issues modify the SDLC workflow layer — prompts and instructions. They share `close-out.prompt.md` as the primary file, making them natural to review together. Splitting them into separate PRs would create merge conflicts on the same file. The reviewer needs prompt engineering and workflow expertise, not C# expertise.

**Execution order:** PG-1 and PG-2 can execute in parallel — they touch completely disjoint file sets.

## References

- `src/Twig/CommandExamples.cs` — Command example registry (50+ commands)
- `src/Twig/Program.cs` lines 866–1087 — `GroupedHelp` class with `KnownCommands` HashSet
- `.github/skills/twig-sdlc/assets/prompts/close-out.prompt.md` — Close-out agent prompt (11 steps)
- `.github/skills/twig-sdlc/assets/twig-sdlc.yaml` — SDLC workflow definition
- `docs/projects/streamline-closeout-fast-path.plan.md` — Prior close-out prompt optimization
- `src/Twig/Hints/HintEngine.cs` — Contextual hint engine (example of help system component)
- `src/Twig.Infrastructure/Git/GitCliService.cs` — Worktree detection implementation

