Implement the following task.
**Task:** #{{ task_manager.output.current_task_id }} — {{ task_manager.output.current_task_title }}
**Description:** {{ task_manager.output.current_task_description }}
**Issue:** #{{ task_manager.output.current_issue_id }} — {{ task_manager.output.current_issue_title }}
**Branch:** {{ pr_group_manager.output.branch_name }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for full context.
{% if task_reviewer is defined and task_reviewer.output and not task_reviewer.output.approved %}
**Previous review — fix these issues:**
{{ task_reviewer.output.feedback }}
{% for issue in task_reviewer.output.issues %}
- {{ issue }}
{% endfor %}
{% endif %}
{% if pr_reviewer is defined and pr_reviewer.output and not pr_reviewer.output.approved %}
**PR review feedback — fix these issues:**
{{ pr_reviewer.output.feedback }}
{% for issue in pr_reviewer.output.issues %}
- {{ issue }}
{% endfor %}
{% endif %}
## Steps
1. **Deep Codebase Research**
   - Analyze the codebase to understand existing patterns and conventions
   - Identify all files that will be created, modified, or deleted for THIS task
   - Review related modules and their interfaces
   - Identify integration points and potential conflicts
   - Document the coding style, naming conventions, and patterns used
   - Add a twig note: `twig note --text "Research: <findings>"`
2. **Implement the changes** following existing conventions
   - Add a twig note: `twig note --text "Impl: <what was done>"`
3. **Write tests** covering the new functionality and edge cases
   - Track edge cases you explicitly handled (for reviewer visibility)
4. **Run tests:** `dotnet test --settings test.runsettings`
   - Add a twig note: `twig note --text "Tests: <count> tests, <coverage>"`
5. **Commit:** `git add -A && git commit -m "<descriptive message>"`
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
