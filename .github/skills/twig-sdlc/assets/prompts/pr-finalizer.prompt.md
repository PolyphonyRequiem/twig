Verify that all PR groups have been merged before close-out proceeds.

**PR Groups from work tree:**
{{ work_tree_seeder.output.pr_groups | json }}

**Completed PRs (claimed by pr_group_manager):**
{{ pr_group_manager.output.completed_prs | json }}

**Completed Issues (claimed by pr_group_manager):**
{{ pr_group_manager.output.completed_issues | json }}

{% if pr_finalizer is defined and pr_finalizer.output %}
**Previous verification attempt #{{ pr_finalizer.output.verification_attempt }}:**
{{ pr_finalizer.output.summary }}
**Previously unmerged:** {{ pr_finalizer.output.unmerged_pr_groups | json }}
{% endif %}

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

### 4. Check for collapsed PR groups
Sometimes pr_group_manager merges multiple PR groups into a single PR (e.g.,
PG-2's work gets included in PG-1's PR). If a PR group has no matching PR but:
- Its branch doesn't exist (deleted after merge)
- No unmerged branches match its name
- The code changes are present on main (verify with `git log --oneline --all --grep="<task keyword>"`)
- The parent Issue is Done with all Tasks Done

Then the PR group was likely **collapsed into another PR**. In this case,
mark it as verified with a note about the collapse.

### 5. Verify Issue states match reality
For each Issue in the work tree:
```
twig set <issue_id> --output json
```
- If the Issue is "Done" but its PR group is NOT in completed_prs AND the code
  is NOT on main → **state integrity violation**
- Record any violations found

## Decision

- If ALL PR groups have merged PRs (or were verified as collapsed) and no state violations → set `verified: true`
- If ANY PR group is genuinely missing merged code:
  - Set `verified: false`
  - Set `unmerged_pr_groups` to the list of PR group names that lack merged PRs
  - Set `orphaned_branches` to any unmerged branches matching those groups
  - Set `state_violations` to any Issues marked Done without merged code

## Attempt Tracking

{% if pr_finalizer is defined and pr_finalizer.output %}
Set `verification_attempt` to {{ pr_finalizer.output.verification_attempt + 1 }}.
{% else %}
Set `verification_attempt` to 1.
{% endif %}

**IMPORTANT:** If this is attempt 3 or higher and verification still fails,
set `verified: true` anyway and include a warning in `summary` explaining
what could not be verified. The close-out agent will record this as an
observation. Do NOT let verification loop indefinitely.

## Output
- `verified` (boolean): True if every PR group has confirmed merged code (or attempt >= 3)
- `unmerged_pr_groups` (array): PR group names that lack merged PRs (empty if verified)
- `orphaned_branches` (array): Feature branches with unmerged work (empty if verified)
- `state_violations` (array): Issues marked Done without merged code (empty if none)
- `summary` (string): Human-readable verification summary
- `verification_attempt` (number): Current attempt count (1-based)
