Manage the implementation lifecycle.
**Work Tree:**
{{ work_tree_seeder.output.work_tree | json }}
**PR Groups:**
{{ work_tree_seeder.output.pr_groups | json }}
**Plan:** {{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}
{% if implementation_manager is defined and implementation_manager.output %}
**Current State:**
- PR Group: {{ implementation_manager.output.current_pr_group }}
- Issue: {{ implementation_manager.output.current_issue_id }}
- Last Task: {{ implementation_manager.output.current_task_id }}
- Completed Tasks: {{ implementation_manager.output.completed_tasks | json }}
- Completed Issues: {{ implementation_manager.output.completed_issues | json }}
- Completed PRs: {{ implementation_manager.output.completed_prs | json }}
{% endif %}
{% if task_reviewer is defined and task_reviewer.output and task_reviewer.output.approved %}
**Task just approved by reviewer.**
1. Close the current task: `twig set <task_id> --output json` then `twig state Done --output json`
2. Add note: `twig note --text "Done: <summary from reviewer>"`
3. Determine next step (see below)
{% endif %}
{% if pr_merge is defined and pr_merge.output and pr_merge.output.merged %}
**PR just merged: {{ pr_merge.output.pr_url }}**
1. Add the PR group to completed_prs
2. Determine next step (see below)
{% endif %}
{% if user_acceptance is defined and user_acceptance.output %}
**User acceptance result:** {{ user_acceptance.output.selected }}
{% if user_acceptance.output.feedback %}
**Feedback:** {{ user_acceptance.output.feedback }}
{% endif %}
If accepted or skipped, close the current issue and proceed.
If changes requested, the feedback describes what to fix — start the next task iteration.
{% endif %}
{% if issue_reviewer is defined and issue_reviewer.output and issue_reviewer.output.approved %}
**Issue review passed.** The issue is ready to close (or go to user_acceptance if needed).
{% endif %}
{% if issue_reviewer is defined and issue_reviewer.output and not issue_reviewer.output.approved %}
**Issue review failed — changes needed:**
{{ issue_reviewer.output.feedback }}
Create a fix task for this issue and set action=implement_task.
{% endif %}
## Task Verification (MANDATORY before every decision)
Do NOT rely solely on completed_tasks from your prior output. Before choosing
the next action, always verify the ground truth:
1. `twig set <current_issue_id> --output json` then `twig tree --output json`
2. Check the state of every child Task — any Task not in state "Done" still
   needs work.
3. Update your completed_tasks list from twig's actual state.
This prevents skipping tasks whose IDs may have shifted between workflow runs.
## Decision Logic
1. If the current issue has Tasks NOT in state "Done" → pick the next undone
   task (by successor order), start it, set action=implement_task
2. If ALL tasks in the current issue are Done AND issue_reviewer has NOT yet reviewed:
   → set action=issue_review (sends to reducer_issue then issue_reviewer)
3. If issue_reviewer approved AND no user_acceptance pending:
   a. Check if this issue has user-facing changes or complex acceptance criteria
   b. If yes and user_acceptance not yet received → set action=needs_acceptance
   c. If user_acceptance received or not needed:
      - Close the issue: `twig set <issue_id>` → `twig state Done` → `twig note`
      - If more issues in current PR group → start next issue and its first task, action=implement_task
      - If PR group done → set action=submit_pr
4. If issue_reviewer rejected → create a fix approach and set action=implement_task
5. If a PR was just merged and more PR groups remain:
   a. Create a new branch: `git checkout -b <branch_name>`
   b. Start first issue and task in the new PR group
   c. Set action=implement_task
6. If all PR groups are done → set action=all_complete
When starting an issue: `twig set <id>` → `twig state Doing` → `twig note --text "Starting: ..."`
When starting a task: `twig set <id>` → `twig state Doing` → `twig note --text "Starting: ..."`
**On first invocation (no prior state):**
- Create branch for first PR group: `git checkout -b <branch_name>`
- Start first issue and first task
- Set action=implement_task
