Review the implementation of task #{{ implementation_manager.output.current_task_id }} — {{ implementation_manager.output.current_task_title }}.
**Task description:** {{ implementation_manager.output.current_task_description }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for acceptance criteria.
**Coder's changes:** {{ coder.output.changes_summary }}
**Files:** {{ coder.output.files_modified | join(", ") }}
**Tests:** {{ coder.output.tests_added | join(", ") }}
**Reducer changes:** {{ reducer_code.output.changes_applied | join(", ") }}
## Review Criteria
1. **Requirements met** — Does the implementation satisfy the task description?
2. **Code quality** — Clean, idiomatic C#? Follows project conventions?
3. **AOT compliance** — No reflection, JSON uses TwigJsonContext?
4. **Test coverage** — Edge cases covered? Tests actually verify behavior?
5. **No regressions** — Run `dotnet test --settings test.runsettings` to verify all tests pass
Provide APPROVE or REQUEST_CHANGES with specific feedback.
