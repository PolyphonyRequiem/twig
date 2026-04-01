Review issue #{{ implementation_manager.output.current_issue_id }} — {{ implementation_manager.output.current_issue_title }}.
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for this issue's acceptance criteria.
**Completed Tasks:** {{ implementation_manager.output.completed_tasks | json }}
**Reducer findings:** {{ reducer_issue.output.findings | join(", ") }}
## Review Criteria
1. **Acceptance criteria met** — Does the combined implementation satisfy the issue's requirements?
2. **Cross-cutting concerns** — Error handling, logging, security consistent across tasks?
3. **Documentation** — Any new public APIs or behaviors documented?
4. **Integration** — Components from different tasks work together correctly?
5. **Test coverage** — Run `dotnet test --settings test.runsettings` to verify all tests pass
Provide APPROVE or REQUEST_CHANGES with specific feedback.
