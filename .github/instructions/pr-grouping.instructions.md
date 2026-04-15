---
description: 'PR grouping strategy guidance for planning agents — sizing, classification, and optimal count heuristics.'
applyTo: '**/*.plan.md'
---

# PR Grouping Strategy

## What PR Groups Are

A **PR group** (PG) is a cross-cutting overlay that organizes plan tasks into
reviewable pull requests. PR groups are **not** a 1:1 mapping to the ADO work item
hierarchy — a single PG may span multiple Issues, and multiple PGs may draw tasks
from the same Issue. The grouping is driven by **review coherence**: each PG should
tell a self-contained story that a reviewer can evaluate without needing context from
other PRs.

## Naming Convention

Use the **PG-N** prefix (e.g., PG-1, PG-2) to identify PR groups in plan documents.

- **Why PG-N, not PR-N**: `PR-N` is ambiguous with GitHub pull request numbers
  (e.g., PR #42 could be the 42nd pull request or PR group 42). `PG` is distinct and
  reads naturally as "PR Group."
- Number sequentially starting from PG-1.
- Reference PG-N identifiers in commit messages and plan status updates.

## Sizing Guardrails

Each PR group should stay within these limits to remain reviewable:

| Metric | Limit | Rationale |
|--------|-------|-----------|
| Lines of code (LoC) | ≤ 2,000 | Beyond this, reviewer fatigue increases and defect detection drops |
| Files changed | ≤ 50 | Large file counts make it hard to hold the change set in working memory |

These are guardrails, not hard rules. Mechanical changes (e.g., bulk renames, XML doc
comments across many files) may exceed file counts while remaining easy to review.
LoC overruns should be justified in the PR description.

## Deep vs Wide Classification

Classify each PR group to set reviewer expectations:

### Deep

Few files with complex logic changes that require careful, line-by-line review.

**Characteristics:**
- Algorithmic changes, new business logic, or architectural modifications
- Typically < 10 files but high cognitive load per file
- Reviewer needs domain context to evaluate correctness

**Examples from Twig plans:**
- A new command implementation with service registration, DI wiring, and tests
- Refactoring a data access layer to support a new query pattern

### Wide

Many files with mechanical or repetitive changes that are straightforward to verify.

**Characteristics:**
- Bulk renames, XML doc comment additions, template expansions
- May touch 20–50+ files but each change follows a predictable pattern
- Reviewer can verify by sampling — checking a few files confirms the pattern

**Examples from Twig plans:**
- Adding XML doc comments to all command classes (PG-1 in the Query Command Closeout)
- Updating `using` directives across the solution after a namespace rename

### Review Implications

| Aspect | Deep | Wide |
|--------|------|------|
| Review time | Longer per file | Shorter per file, longer overall |
| Review strategy | Line-by-line | Sample-based with spot checks |
| Risk of defects | Higher — logic errors | Lower — pattern errors |
| Merge conflict risk | Higher — concentrated changes | Lower — distributed changes |

## Optimal PR Count Guidance

### The 2-PR Sweet Spot

Two PR groups per epic is the observed sweet spot for Twig's completed epics (limited sample — treat as advisory, not prescriptive). With 2 PGs:

- **Parallelism**: One PG can be in review while the other is being implemented.
- **Isolation**: A blocking review comment on PG-1 doesn't stall PG-2.
- **Cognitive overhead**: The plan author and reviewer each hold at most two
  groupings in mind — manageable without written cross-references.
- **Merge sequencing**: Two PRs have exactly one merge dependency (PG-2 rebases
  onto PG-1) — trivial to manage.

### The 3-PR Threshold

Three PR groups is the threshold where cognitive overhead increases meaningfully.
At 3 PGs:

- **Merge graph complexity**: Three PRs have up to three pairwise dependencies.
  Rebase chains become non-trivial (PG-3 must rebase onto PG-2, which must rebase
  onto PG-1).
- **Context switching**: The plan author must track three independent review cycles,
  each with its own comment threads and iteration history.
- **Review fatigue**: Reviewers must context-switch between three related but
  distinct change sets, increasing the risk of rubber-stamping later PRs.

Three PGs are sometimes necessary — for example, when an epic has genuinely
independent work streams that would create merge conflicts if combined. But reaching
for 3 PGs should be a conscious decision, not the default.

### When to Split vs Consolidate

| Situation | Recommendation |
|-----------|----------------|
| 1 PG with > 2,000 LoC | Split into 2 PGs along a natural seam (e.g., implementation vs tests) |
| 2 PGs with < 500 LoC each | Consider consolidating into 1 PG if the changes are related |
| 3 PGs planned | Verify each PG is genuinely independent — if two share significant context, merge them |
| 4+ PGs planned | Strong signal to re-evaluate — can any be combined without exceeding sizing guardrails? |

## Anti-Patterns

### Over-Fragmentation (4+ PRs)

Splitting an epic into 4 or more PRs creates compounding overhead:
- Each additional PR adds merge sequencing complexity
- Reviewers lose the big picture across many small PRs
- Context-switching costs dominate implementation time
- CI pipeline runs multiply, increasing feedback loop time

**Remedy**: Consolidate related PGs until you're at 2–3 groups.

### Mega-PR (Single PR for Entire Epic)

Shipping the entire epic as one PR:
- Creates an unreviewable wall of changes
- Makes it impossible to merge early, stable work while iterating on later tasks
- A single blocking review comment stalls the entire epic

**Remedy**: Identify a natural seam — typically "infrastructure/models" vs
"commands/features" vs "documentation/tests" — and split into 2 PGs.

### Misaligned Grouping

Grouping by ADO hierarchy (one PR per Issue) rather than by review coherence:
- ADO Issues represent *what* to build; PR groups represent *how to review*
- A PR that mixes unrelated changes from different Issues is hard to review
- A PR that splits a single coherent change across two PRs forces reviewers to
  read both to understand either

**Remedy**: Group by what makes sense to review together, not by what lives under
the same ADO parent.
