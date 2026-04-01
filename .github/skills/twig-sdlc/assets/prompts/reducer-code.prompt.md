Review and simplify the implementation for task #{{ implementation_manager.output.current_task_id }}.
**Files modified:**
{% for file in coder.output.files_modified %}
- {{ file }}
{% endfor %}
**Changes:** {{ coder.output.changes_summary }}
Apply **code-level reduction** principles:
1. Remove dead code, unused imports, stale comments
2. Inline unnecessary abstractions (single-use interfaces, wrapper classes)
3. Remove over-defensive coding (impossible null checks, unreachable catches)
4. Simplify verbose patterns (explicit loops → LINQ, multi-step → single expression)
5. Eliminate premature optimization
If you make changes:
- Apply the edits directly to files
- Run tests to verify nothing breaks: `dotnet test --settings test.runsettings`
- Fold changes into the coder's last commit: `git add -A && git commit --amend --no-edit`
  This keeps one commit per task instead of separate "reduce:" commits.
- Add note: `twig note --text "Reducer: <what was simplified>"`
If the code is already lean, report no changes and move on.
