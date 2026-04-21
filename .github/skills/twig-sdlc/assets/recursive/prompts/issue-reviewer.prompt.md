Review issue #{{ workflow.input.issue_id }} — {{ workflow.input.issue_title }}.
**Plan:** Read `{{ workflow.input.plan_path }}` for this issue's acceptance criteria.
**Completed Tasks:** {{ workflow.input.completed_tasks }}
{% if reducer_issue is defined %}
**Reducer findings:** {{ reducer_issue.output.findings | join(", ") }}
{% endif %}

## Review Tasks

1. **Acceptance Criteria** — Does the combined implementation satisfy the issue's requirements?
2. **Cross-Cutting Concerns** — Error handling, logging, security consistent across tasks?
3. **Integration** — Components from different tasks work together correctly?
4. **Documentation** — Any new public APIs or behaviors documented?
5. **Test Coverage** — Run `dotnet test --settings test.runsettings` to verify all tests pass

Provide APPROVE or REQUEST_CHANGES with specific feedback.
Also note what was done well — strengths help reinforce good patterns.
