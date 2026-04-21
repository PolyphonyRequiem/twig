Perform a post-issue reduction sweep for issue #{{ workflow.input.issue_id }} — {{ workflow.input.issue_title }}.
**Plan:** Read `{{ workflow.input.plan_path }}` for this issue's scope.
**Completed Tasks:** {{ workflow.input.completed_tasks }}
Review ALL files changed across this issue's tasks for cross-task opportunities:
1. Duplicate patterns introduced by separate tasks
2. Abstractions that can be consolidated now that all tasks are done
3. Dead code left behind by incremental implementation
4. Test helpers or utilities used only once
5. Duplicate or overlapping test cases across tasks
Accumulate ALL findings and apply them in a SINGLE pass. Then:
- Run tests: `dotnet test --settings test.runsettings`
- Make ONE commit with all reductions: `git add -A && git commit -m "reduce: post-issue sweep for #{{ workflow.input.issue_id }} — <N> simplifications"`
- Add ONE note: `twig note --text "Reducer: <summary of all changes>"`
If nothing to reduce, say so.
