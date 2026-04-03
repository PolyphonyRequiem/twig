Manage PR group lifecycle.

**Work Tree:**
{{ work_tree_seeder.output.work_tree | json }}

**PR Groups:**
{{ work_tree_seeder.output.pr_groups | json }}

**Plan:** {{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}

{% if pr_group_manager is defined and pr_group_manager.output %}
**Current State:**
- PR Group: {{ pr_group_manager.output.current_pr_group }}
- Branch: {{ pr_group_manager.output.branch_name }}
- Completed Issues: {{ pr_group_manager.output.completed_issues | json }}
- Completed PRs: {{ pr_group_manager.output.completed_prs | json }}
{% endif %}

{% if pr_merge is defined and pr_merge.output and pr_merge.output.merged %}
**PR just merged: {{ pr_merge.output.pr_url }}**

The code is now on main. You MUST now close all issues in this PR group:
{% if task_manager is defined and task_manager.output %}
**Reviewed Issues to close:** {{ task_manager.output.reviewed_issues | json }}
{% endif %}

For each issue:
1. `twig set <issue_id> --output json`
2. `twig note --text "Done: closed after PR #<number> merged to main" --output json`
3. `twig state Done --output json`

Then determine next step (see Decision Logic below).
{% endif %}

{% if task_manager is defined and task_manager.output and task_manager.output.action == 'pr_group_ready' %}
**All issues in current PR group are reviewed and ready for PR submission.**
Set action=submit_pr to send to the PR pipeline.
{% endif %}

## Decision Logic

1. **First invocation (no prior state):**
   - Create branch for first PR group: `git checkout -b <branch_name>`
   - Start first issue: `twig set <id> --output json` → `twig state Doing --output json`
   - Set action=start_tasks

2. **task_manager returned pr_group_ready:**
   - Set action=submit_pr

3. **PR just merged:**
   - Close all reviewed issues in this PR group (see above)
   - Add PR group to completed_prs
   - If more PR groups remain:
     a. Create new branch: `git checkout main && git pull && git checkout -b <next_branch_name>`
     b. Set action=start_tasks
   - If all PR groups complete:
     Set action=all_complete

## CRITICAL CONSTRAINT

You MUST NOT close any Issue until AFTER pr_merge confirms the PR is merged.
This is the structural enforcement that prevents the "code complete but not
shipped" failures observed in prior SDLC runs (#1338, #1394). The two prior
runs both had agents close Issues before PRs were merged, causing ADO state to
diverge from actual code delivery. This split exists specifically to prevent that.
