Perform a pre-PR reduction sweep for PR group {{ pr_group_manager.output.current_pr_group }}.
**Epic:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Branch:** {{ pr_group_manager.output.branch_name }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}`
Review ALL changes in this PR group (all commits on this branch vs main):
1. `git diff main...HEAD --stat` to see all files changed
2. `git diff main...HEAD` to review the full diff
Apply **code-level reduction** across the PR group:
- Cross-file dead code (imports, methods referenced by removed code)
- Stale references to renamed/removed methods (grep for old names)
- Duplicate patterns introduced across different tasks
- Abstractions that turned out unnecessary in the final shape
- Test helpers that are used only once
If you make changes:
- Run tests: `dotnet test --settings test.runsettings`
- Commit: `git add -A && git commit -m "reduce: pre-PR sweep for {{ pr_group_manager.output.current_pr_group }}"`
If nothing to reduce, say so.
