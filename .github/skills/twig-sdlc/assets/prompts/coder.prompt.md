Implement the following task.
**Task:** #{{ implementation_manager.output.current_task_id }} — {{ implementation_manager.output.current_task_title }}
**Description:** {{ implementation_manager.output.current_task_description }}
**Issue:** #{{ implementation_manager.output.current_issue_id }} — {{ implementation_manager.output.current_issue_title }}
**Branch:** {{ implementation_manager.output.branch_name }}
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
1. Research the codebase to understand affected files and patterns
2. Add a twig note: `twig note --text "Research: <findings>"`
3. Implement the changes following existing conventions
4. Add a twig note: `twig note --text "Impl: <what was done>"`
5. Write tests covering the new functionality and edge cases
6. Run tests: `dotnet test --settings test.runsettings`
7. Add a twig note: `twig note --text "Tests: <count> tests, <coverage>"`
8. Commit: `git add -A && git commit -m "<descriptive message>"`
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
