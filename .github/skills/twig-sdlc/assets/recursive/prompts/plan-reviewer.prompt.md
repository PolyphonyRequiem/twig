Review the task decomposition for Issue #{{ workflow.input.work_item_id }} — {{ workflow.input.title }}.
**Plan:** Read `{{ workflow.input.plan_path }}` for this issue's scope.

Check the ADO work items:
```
twig set {{ workflow.input.work_item_id }} --output json
twig tree --output json
```

## Review Criteria

1. **Completeness** — Do the Tasks cover everything in the Issue description?
2. **Granularity** — Is each Task independently committable? Not too big, not too small?
3. **Descriptions** — Does each Task specify files, changes, and expected behavior?
4. **Dependencies** — Are Task ordering and dependencies correct?
5. **Test coverage** — Are there Tasks for tests where needed?

Score 0-100 and list any critical issues that must be fixed.
Provide APPROVE (score >= 80, no critical issues) or REQUEST_CHANGES.
