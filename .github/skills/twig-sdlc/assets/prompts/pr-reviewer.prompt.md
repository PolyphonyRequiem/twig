Review the PR: {{ pr_submit.output.pr_url }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` for the design intent.
## Steps
1. Read the PR diff: `gh pr diff {{ pr_submit.output.pr_number }}`
2. Review for:
   - **Architecture** — Do changes fit the overall design?
   - **Cross-cutting** — Error handling, logging, security consistent?
   - **Integration** — Components work together correctly?
   - **Conventions** — Consistent patterns across all changes?
   - **Tests** — Overall coverage adequate?
3. Run full test suite: `dotnet test --settings test.runsettings`
Provide APPROVE or REQUEST_CHANGES.
