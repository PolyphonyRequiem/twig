Implement the following task.
**Task:** #{{ task_manager.output.current_task_id }} — {{ task_manager.output.current_task_title }}
**Description:** {{ task_manager.output.current_task_description }}
**Issue:** #{{ task_manager.output.current_issue_id }} — {{ task_manager.output.current_issue_title }}
**Branch:** {{ pr_group_manager.output.branch_name }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for full context.
{% if task_reviewer is defined and task_reviewer.output and not task_reviewer.output.approved %}
**Previous review — fix these issues:**
{{ task_reviewer.output.feedback | default('') }}
{% for issue in task_reviewer.output.issues %}
- {{ issue }}
{% endfor %}
{% endif %}
{% if pr_reviewer is defined and pr_reviewer.output and not pr_reviewer.output.approved %}
**PR review feedback — fix these issues:**
{{ pr_reviewer.output.feedback | default('') }}
{% for issue in pr_reviewer.output.issues %}
- {{ issue }}
{% endfor %}
{% endif %}
## Steps

### Step 0 — Prior State Check (< 3 minutes, MANDATORY)
Before doing ANY research or implementation, check if this task was already worked on:
```
git --no-pager log --oneline -10
git --no-pager diff --stat HEAD
git --no-pager status --short
```
**If commits already exist for this task** (matching the task ID, issue, or description):
- Verify the existing work is correct with *targeted* spot-checks — NOT a full re-verification
- If it looks good: run `dotnet test --settings test.runsettings`, commit any uncommitted changes, and go straight to **Output**
- If it has problems: fix only what's broken, don't redo from scratch
- **Budget: spend ≤ 5 minutes verifying prior work. Trust prior commits unless you find concrete evidence of breakage.**

**If no prior work exists**, proceed to Step 1.

### Step 1 — Targeted Research (< 10 minutes)
Research ONLY what you need for THIS task — not the whole codebase:
- Read the plan file for this task's specific section
- Identify files to create/modify (use `grep` and `glob`, not exploratory shell commands)
- Check the conventions of 1-2 similar existing files as reference
- Add a twig note: `twig note --text "Research: <findings>"`

Do NOT: enumerate all modules, review every interface, or catalog the entire codebase.

### Step 2 — Implement
Implement the changes following existing conventions.
- Add a twig note: `twig note --text "Impl: <what was done>"`

### Step 3 — Write Tests
Write tests covering the new functionality and edge cases.
- Track edge cases you explicitly handled (for reviewer visibility)

### Step 4 — Run Tests
`dotnet test --settings test.runsettings`
- Add a twig note: `twig note --text "Tests: <count> passed"`

### Step 5 — Commit
`git add -A && git commit -m "<descriptive message>"`

Do NOT implement anything beyond this single task.

## Pre-Review Checklist (avoid review round-trips)
Before committing, self-check against these criteria that the reviewer will enforce:
- [ ] Requirements met — implementation satisfies the task description
- [ ] Code quality — clean, idiomatic C#, follows project conventions (sealed classes, primary constructors)
- [ ] AOT compliance — no reflection, all JSON uses TwigJsonContext, no dynamic loading
- [ ] Test coverage — edge cases covered, tests verify behavior not implementation
- [ ] No stale references — renamed/removed methods updated at ALL call sites
- [ ] Builds clean — `dotnet build` produces zero warnings (TreatWarningsAsErrors)
- [ ] All tests pass — `dotnet test --settings test.runsettings` green
