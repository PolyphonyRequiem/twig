Verify that all PR groups have been merged before close-out proceeds.

**PR Groups from work tree:**
{{ work_tree_seeder.output.pr_groups | json }}

**Completed PRs (claimed by pr_group_manager):**
{{ pr_group_manager.output.completed_prs | json }}

**Completed Issues (claimed by pr_group_manager):**
{{ pr_group_manager.output.completed_issues | json }}

## Verification Steps

### 1. Cross-reference PR groups against merged PRs
For EACH PR group in the work tree:
- Check if it appears in completed_prs
- If missing → this PR group was never submitted or merged

### 2. Check for unmerged feature branches
```
git checkout main && git pull
git branch --no-merged main
```
Cross-reference any unmerged branches against the PR groups' `branch_name_suggestion`.
If a branch matches a PR group that should be complete, that group's work is orphaned.

### 3. Verify merged PRs via GitHub
For each PR number in completed_prs:
```
gh pr view <pr_number> --json state --jq '.state'
```
Must return "MERGED".

### 4. Verify Issue states match reality
For each Issue in the work tree:
```
twig set <issue_id> --output json
```
- If the Issue is "Done" but its PR group is NOT in completed_prs → **state integrity violation**
- Record any violations found

## Decision

- If ALL PR groups have merged PRs and no state violations → set `verified: true`
- If ANY PR group is missing a merged PR:
  - Set `verified: false`
  - Set `unmerged_pr_groups` to the list of PR group names that lack merged PRs
  - Set `orphaned_branches` to any unmerged branches matching those groups
  - Set `state_violations` to any Issues marked Done without merged code

## Output
- `verified` (boolean): True only if every PR group has a confirmed merged PR
- `unmerged_pr_groups` (array): PR group names that lack merged PRs (empty if verified)
- `orphaned_branches` (array): Feature branches with unmerged work (empty if verified)
- `state_violations` (array): Issues marked Done without merged code (empty if none)
- `summary` (string): Human-readable verification summary
