Perform a post-issue reduction sweep for issue #{{ implementation_manager.output.current_issue_id }} — {{ implementation_manager.output.current_issue_title }}.
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for this issue's scope.
**Completed Tasks:** {{ implementation_manager.output.completed_tasks | json }}
Review ALL files changed across this issue's tasks for cross-task opportunities:
1. Duplicate patterns introduced by separate tasks
2. Abstractions that can be consolidated now that all tasks are done
3. Dead code left behind by incremental implementation
4. Test helpers or utilities used only once
5. Duplicate or overlapping test cases across tasks
Accumulate ALL findings and apply them in a SINGLE pass. Then:
- Run tests: `dotnet test --settings test.runsettings`
- Make ONE commit with all reductions: `git add -A && git commit -m "reduce: post-issue sweep for #{{ implementation_manager.output.current_issue_id }} — <N> simplifications"`
- Add ONE note: `twig note --text "Reducer: <summary of all changes>"`
If nothing to reduce, say so.
