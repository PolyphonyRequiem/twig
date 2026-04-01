Review the PR: {{ pr_submit.output.pr_url }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for the design intent.

## Holistic Review Tasks

1. **Architecture Review**
   - Do changes fit the overall design from the plan?
   - Proper separation of concerns?
   - Components integrate correctly?

2. **Cross-Cutting Concerns**
   - Error handling, logging, security consistent across all changes?
   - Resource management (disposal, cancellation tokens)?

3. **Code Consistency**
   - Consistent patterns across all changes?
   - Naming conventions uniform?
   - Code style consistent with existing codebase?

4. **Test Coverage**
   - Overall coverage adequate across the PR?
   - Integration between components tested?
   - Run full test suite: `dotnet test --settings test.runsettings`

5. **Git History**
   - Review all commits: `gh pr diff {{ pr_submit.output.pr_number }}`
   - Commit messages clear and descriptive?

Provide APPROVE or REQUEST_CHANGES.
Also note what was done well — strengths help reinforce good patterns.
