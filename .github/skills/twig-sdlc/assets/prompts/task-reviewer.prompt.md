Review the implementation of task #{{ task_manager.output.current_task_id }} — {{ task_manager.output.current_task_title }}.
**Task description:** {{ task_manager.output.current_task_description }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for acceptance criteria.
**Coder's changes:** {{ coder.output.changes_summary }}
**Files:** {{ coder.output.files_modified | join(", ") }}
**Tests:** {{ coder.output.tests_added | join(", ") }}
{% if coder.output.edge_cases_handled | length > 0 %}
**Edge cases handled:** {{ coder.output.edge_cases_handled | join(", ") }}
{% endif %}

## Review Tasks

1. **Requirements Verification**
   - Verify that all requirements from the task description are met
   - Check that acceptance criteria are satisfied
   - Ensure no requirements were missed

2. **Code Quality**
   - Clean, idiomatic C#? Follows project conventions (sealed classes, primary constructors)?
   - No reflection, JSON uses TwigJsonContext? AOT-compatible?
   - Proper error handling at system boundaries?

3. **Edge Case Verification**
   - Confirm edge cases were handled appropriately
   - Check error handling is comprehensive
   - Verify no stale references — renamed/removed methods updated at ALL call sites

4. **Test Coverage**
   - Verify tests cover the new functionality
   - Check that tests cover edge cases and error conditions
   - Run `dotnet test --settings test.runsettings` to verify all tests pass

Provide APPROVE or REQUEST_CHANGES with specific feedback.
Also note what was done well — strengths help reinforce good patterns.
