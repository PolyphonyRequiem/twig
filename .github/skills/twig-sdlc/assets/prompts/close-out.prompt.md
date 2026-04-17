Close out the SDLC workflow.
**Epic:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Completed Issues:** {{ pr_group_manager.output.completed_issues | json }}
**Completed PRs:** {{ pr_group_manager.output.completed_prs | json }}
**Plan:** {{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}

**PR Finalization Verification:**
- Verified: {{ pr_finalizer.output.verified }}
- Summary: {{ pr_finalizer.output.summary }}
{% if pr_finalizer.output.state_violations | length > 0 %}- State Violations: {{ pr_finalizer.output.state_violations | json }}{% endif %}

## Ownership Convention
Implementing agents (coder, task_manager) own Task state transitions.
pr_group_manager owns Issue closure (only after PR merge).
The close_out agent owns the **Epic-level** transition to Done.
Do NOT assume implementing agents have already transitioned the Epic.

## Worktree Conventions

When the SDLC workflow runs inside a git worktree (common for parallel PR group
development), the following conventions enable deterministic branch discovery:

**Branch naming**: Each PR group branch uses the `branch_name_suggestion` from
`work_tree_seeder.output.pr_groups`. By convention, branches follow the pattern
`feature/{slug}` or `feature/{work-item-id}-{slug}` (e.g.,
`feature/validate-command-examples`, `feature/1744-closeout-skip-when-done`).

**Linked worktree detection**: Compare `git rev-parse --git-common-dir` with
`git rev-parse --git-dir`. If they differ (and common-dir ≠ `.git`), the
current directory is a linked worktree. In a linked worktree,
`git checkout main` will fail if main is already checked out in the primary
worktree — use `git fetch origin main` instead (see Step 1).

## Steps
0. **Sync local cache** (prevents stale-state conflicts from multi-agent workflows):
   - `twig sync --output json` — flush pending changes and pull all remote state into local DB
   - This ensures issues/tasks transitioned by other agents are reflected locally
   - If sync fails with a transient ADO error, retry up to 3 times with 5-second delays
0b. **Fast-path: Already-Done check** (skip redundant verification when re-running):
   - `twig set {{ intake.output.epic_id }} --output json` — read the current state
   - If the state is already "Done":
     1. Record the fast-path decision:
        `twig note --text "Fast-path: Epic/Issue is already Done — skipping PR/branch/child verification (Steps 1–4) and proceeding directly to observations."`
     2. Set `epic_completed: false` (the agent did not perform the transition)
     3. **SKIP directly to Step 5** — do NOT execute Steps 1, 1b, 1c, 2, 3, or 4
   - If the state is NOT "Done": continue with Step 1 (normal flow)

   > **Why safe:** Verification was already performed during the original close-out run that transitioned the item to Done.
   > Additionally, the upstream `pr_finalizer` gate already confirmed PR merge state; re-running verification risks false-positive STOP conditions.
   > **When this triggers:** Re-runs, retries, manual workflow restarts, or manual Done transitions.
   > **What is NOT skipped:** Steps 5–10 always execute (observations, git push, tagging).
1. **Verify all PRs are merged** (guard against premature close-out):
   - The pr_finalizer agent has already verified PR group completeness upstream.
     Review its `summary` and `state_violations` above.
   - As a defense-in-depth check, also verify directly:
     For each PR in the completed list:
     `gh pr view <pr_number> --json state --jq '.state'` — must be "MERGED"
   - If any PR is not merged, STOP and report the issue — do not proceed
   - Verify main has the merged commits (worktree-aware):
     ```bash
     # Detect linked worktree
     common_dir=$(git rev-parse --git-common-dir)
     git_dir=$(git rev-parse --git-dir)
     if [ "$common_dir" != "$git_dir" ] && [ "$common_dir" != ".git" ]; then
       # Linked worktree — cannot checkout main (may be checked out elsewhere)
       git fetch origin main
       git log origin/main --oneline -5
     else
       # Standard clone — safe to checkout
       git checkout main && git pull
     fi
     ```
1b. **Verify no unmerged feature branches** (guard against orphaned work):
   - Run: `git branch --no-merged main`
   - Cross-reference against the work tree's PR groups (not just the plan):
     {{ work_tree_seeder.output.pr_groups | json }}
   - Match unmerged branches to PR groups by naming convention: each PR group's
     `branch_name_suggestion` (e.g., `feature/validate-command-examples`) should
     appear in the unmerged list only if the PR group is still in progress. For
     completed PR groups, the branch should be absent (deleted after merge) or
     fully merged into main.
   - Also check for worktree-associated branches: run `git worktree list` and
     verify that any worktree branches correspond to known PR groups. A worktree
     whose branch doesn't match any PR group's `branch_name_suggestion` is
     outside this workflow's scope — note it but do not block on it.
   - If ANY branch matches a planned PR group that should be complete, STOP and
     report — code exists on a branch that was never PR'd or merged
1c. **Verify all child items are Done** (guard against premature Epic closure):
   - `twig set {{ intake.output.epic_id }} --output json`
   - `twig tree --output json` — inspect all children
   - If ANY child Issue or Task is NOT in state "Done", STOP and report which
     items are still open — do NOT transition the Epic
2. **Check current state (idempotency):**
   - `twig set {{ intake.output.epic_id }} --output json` — read the current state
   - `git log --oneline -10` — check if a close commit already exists
   - If state is already "Done" AND a close commit exists, skip steps 3-4 and go to step 5
3. **Transition the Epic to Done** (only if not already Done):
   - `twig note --text "All work complete. <progress summary>"`
   - `twig state Done --output json`
4. **Update the plan document** at `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}`:
   - Change the Status line from its current value to `> **Status**: ✅ Done`
   - Mark all Issues and Tasks as DONE in the plan file (use "Epics and Tasks" for Epic-level close-outs)
   - Add a **≤10-line** plan completion block containing ONLY:
     1. Completion date
     2. A 1–2 sentence summary of what was delivered
     3. The ADO work item reference (e.g., `AB#<id>`)
   - **Do NOT inline full observations into the plan document.** Full observations,
     improvement suggestions, and detailed analysis belong exclusively in the ADO
     note recorded in Step 7.
5. **Full workflow git log and diff statistics** — capture commit history and aggregate change metrics:
   - `git log --oneline --all` (or scope to the workflow's branches/main)
   - Note commit cadence, rework patterns, time-to-completion indicators
   - **Tag-based diff statistics** — summarize the scope of changes since the last release:
     ```bash
     # Get the most recent release tag (created by the previous close-out's Step 10)
     prev_tag=$(git tag -l "v*" --sort=-version:refname | head -1)
     if [ -n "$prev_tag" ]; then
       # Capture aggregate diff stats from baseline to current HEAD
       git diff --stat "$prev_tag"..HEAD
       # Capture commit count
       git rev-list --count "$prev_tag"..HEAD
     fi
     ```
   - Format the results into a summary table:
     | Metric | Value |
     |--------|-------|
     | Files changed | *(from `git diff --stat` last line)* |
     | Lines added | *(insertions from `git diff --stat`)* |
     | Lines removed | *(deletions from `git diff --stat`)* |
     | Commit count | *(from `git rev-list --count`)* |
   - **Fallback**: If no `v*` tags exist (first-ever close-out, new repository) or
     `git diff` fails (shallow clone, detached HEAD), skip the statistics table and
     note: **"diff stats unavailable — no baseline tag found"**
   - **Important**: The diff range MUST use the previous release tag as baseline — NOT
     `origin/main..HEAD` — because after Step 1's `git checkout main && git pull`,
     HEAD equals `origin/main` and that range would be empty.
6. **Produce meta-observations:**
   Reflect on the entire workflow execution:
   - What went well? Which agents performed effectively?
   - Where did agents struggle? (hallucinations, wrong tools, missed context)
   - What workflow improvements would help future runs?
   - Were the PR groupings effective?
   - Was the plan accurate, or did implementation diverge significantly?
   - Commit cadence and rework analysis from the git log
7. **Record observations in ADO** — add a twig note summarizing key meta-observations:
   - `twig set {{ intake.output.epic_id }} --output json`
   - `twig note --text "Workflow observations: <2-3 sentence summary of what went well, what struggled, and top improvement>"`
8. **Ensure all work is upstream:**
   - `git push` — push any pending commits (e.g., reduction sweeps, plan status updates)
   - If push fails (nothing to push), that's fine — continue
9. **Final commit** (only if there are uncommitted changes):
   - `git diff --stat HEAD` — check for changes
   - If changes exist: `git add -A && git commit -m "close: {{ intake.output.epic_title }}" && git push`
   - If no changes: skip commit
10. **Tag the release** (after all pushes are complete):
   - Get the latest tag: `git tag -l "v*" --sort=-version:refname | head -1`
   - Parse the version components (major.minor.patch)
   - Determine increment based on item type ({{ intake.output.item_type }}):
     - **Issue** → increment patch (e.g., v0.25.2 → v0.25.3)
     - **Epic** → increment minor, reset patch (e.g., v0.25.2 → v0.26.0)
   - Create the tag: `git tag v<new_version>`
   - Push the tag: `git push origin v<new_version>`
