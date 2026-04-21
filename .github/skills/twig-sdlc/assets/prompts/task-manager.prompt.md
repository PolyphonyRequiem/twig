Manage tasks within the current PR group.

**PR Group:** {{ pr_group_manager.output.current_pr_group }}
**Branch:** {{ pr_group_manager.output.branch_name }}
**Issues in PR Group:** {{ pr_group_manager.output.pr_group_issue_ids | json }}
**Tasks in PR Group:** {{ pr_group_manager.output.pr_group_task_ids | json }}

**Work Tree:**
{{ work_tree_seeder.output.work_tree | json }}

**Plan:** {{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}

{% if task_manager is defined and task_manager.output %}
**Current State:**
- Issue: #{{ task_manager.output.current_issue_id }} — {{ task_manager.output.current_issue_title }}
- Last Task: #{{ task_manager.output.current_task_id }}
- Completed Tasks: {{ task_manager.output.completed_tasks | json }}
- Reviewed Issues: {{ task_manager.output.reviewed_issues | json }}
{% endif %}

{% if task_reviewer is defined and task_reviewer.output and task_reviewer.output.approved %}
**Task just approved by reviewer.**
1. Close the current task: `twig set <task_id> --output json` then `twig state Done --output json`
2. Add note: `twig note --text "Done: <summary from reviewer>" --output json`
3. Determine next step (see below)
{% endif %}

{% set ir = issue_reviewer if issue_reviewer is defined else (issue_review if issue_review is defined else none) %}
{% if ir is not none and ir.output and ir.output.approved %}
**Issue review passed.** Add this issue to reviewed_issues.
Check if this issue needs user acceptance (user-facing changes or complex acceptance criteria).
{% endif %}

{% if ir is not none and ir.output and not ir.output.approved %}
**Issue review failed — changes needed:**
{{ ir.output.feedback | default('') }}
Identify which task needs fixing based on the feedback. Run `twig tree --output json`
to find the task, then set action=implement_task with that task's ID and description
updated to include the reviewer feedback.
{% endif %}

{% if user_acceptance is defined and user_acceptance.output %}
**User acceptance result:** {{ user_acceptance.output.selected }}
{% if user_acceptance.output.selected == 'changes' %}
**Feedback:** {{ user_acceptance.output.feedback | default('') }}
{% endif %}
If accepted or skipped: add this issue to reviewed_issues and proceed.
If changes requested: create a fix task and set action=implement_task.
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
2. If ALL tasks in the current issue are Done AND issue review has NOT yet
   been done for this issue → set action=issue_review
3. If issue review approved:
   a. Check if this issue has user-facing changes or complex acceptance criteria
   b. If yes and user_acceptance not yet received → set action=needs_acceptance
   c. If user_acceptance received or not needed → add issue to reviewed_issues
4. If issue review rejected → create a fix approach and set action=implement_task
5. If more issues in this PR group need work → start next issue's first task,
   set action=implement_task
6. If ALL issues in this PR group are in reviewed_issues → set action=pr_group_ready

When starting an issue: `twig set <id> --output json` → `twig state Doing --output json` → `twig note --text "Starting: ..." --output json`
When starting a task: `twig set <id> --output json` → `twig state Doing --output json` → `twig note --text "Starting: ..." --output json`

**On first invocation (from pr_group_manager):**
- Start first issue and first task in the PR group
- Set action=implement_task

## CRITICAL CONSTRAINTS

- You MUST NOT close Issues — only Tasks. Issues are closed by pr_group_manager
  after PR merge. If you close an Issue, the ADO state will diverge from code
  delivery status.
- **Before running `twig state Done`**, verify the item is a Task:
  `twig set <id> --output json` → check `"type"` is `"Task"`. If it's an Issue,
  STOP — you do not own Issue state transitions.
- You MUST NOT create branches or submit PRs.
- When all issues pass review, return pr_group_ready — do not try to proceed further.

## Pre-Handoff Verification (MANDATORY before returning pr_group_ready)

Before setting action=pr_group_ready, run a final state audit:
1. For EACH issue in the PR group:
   - `twig set <issue_id> --output json` — verify the Issue is still in "Doing"
     (NOT "Done"). If any Issue is "Done", you have a bug — report it.
   - `twig tree --output json` — verify all child Tasks are in state "Done"
2. Only after confirming: all Issues still "Doing", all Tasks "Done", and all
   issues in reviewed_issues — set action=pr_group_ready.

## Output Field Rules (MANDATORY)

- `current_task_id` MUST always be a number, never null:
  - When action=implement_task: the ADO Task ID to implement
  - When action=issue_review, needs_acceptance, or pr_group_ready: use 0
- `current_task_title` and `current_task_description`: use empty string "" when not applicable
