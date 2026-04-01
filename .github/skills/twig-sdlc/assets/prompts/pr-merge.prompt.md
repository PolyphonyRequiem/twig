Merge the approved PR.
**PR:** {{ pr_submit.output.pr_url }} (#{{ pr_submit.output.pr_number }})
**Plan:** {{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}
## Steps
1. Merge: `gh pr merge {{ pr_submit.output.pr_number }} --merge --delete-branch`
2. Switch to main: `git checkout main && git pull`
3. Verify clean state: `git status`
4. **Update plan status** — read the plan file and change its `> **Status**:` line
   to `> **Status**: ✅ Done` (if this was the final PR group) or
   `> **Status**: 🔨 In Progress — <N>/<M> PR groups merged` (if more remain).
   Commit the change: `git add -A && git commit -m "docs: update plan status after PR merge"`
