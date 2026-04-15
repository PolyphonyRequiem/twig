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
## Steps
0. **Sync local cache** (prevents stale-state conflicts from multi-agent workflows):
   - `twig sync --output json` — flush pending changes and pull all remote state into local DB
   - This ensures issues/tasks transitioned by other agents are reflected locally
   - If sync fails with a transient ADO error, retry up to 3 times with 5-second delays
1. **Verify all PRs are merged** (guard against premature close-out):
   - The pr_finalizer agent has already verified PR group completeness upstream.
     Review its `summary` and `state_violations` above.
   - As a defense-in-depth check, also verify directly:
     For each PR in the completed list:
     `gh pr view <pr_number> --json state --jq '.state'` — must be "MERGED"
   - If any PR is not merged, STOP and report the issue — do not proceed
   - Also verify main has the commits: `git checkout main && git pull`
1b. **Verify no unmerged feature branches** (guard against orphaned work):
   - Run: `git branch --no-merged main`
   - Cross-reference against the work tree's PR groups (not just the plan):
     {{ work_tree_seeder.output.pr_groups | json }}
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
   - Mark all Epics and Tasks as DONE in the plan file
   - Add a completion section with date and summary
5. **Full workflow git log** — for richer observations, capture the entire commit history:
   - `git log --oneline --all` (or scope to the workflow's branches/main)
   - Note commit cadence, rework patterns, time-to-completion indicators
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
