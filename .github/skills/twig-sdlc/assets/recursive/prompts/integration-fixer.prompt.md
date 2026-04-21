Fix integration issues found after merging all PR groups.

**Epic:** #{{ workflow.input.epic_id }} — {{ workflow.input.epic_title }}
**Branch:** {{ workflow.input.branch_name }}
**Plan:** Read `{{ workflow.input.plan_path }}` for full context.
**Integration issues found:**
{{ workflow.input.integration_findings }}

## Steps

### Step 1 — Assess
Review the specific issues listed above. For each:
- Identify the root cause (which PRs created the conflict)
- Determine the minimal fix

### Step 2 — Fix
Apply surgical fixes. Do NOT refactor unrelated code.
- Run `dotnet build` after each significant fix
- Add a twig note: `twig note --text "Integration fix: <summary>"`

### Step 3 — Test
Run the full test suite to verify nothing is broken:
`dotnet test --settings test.runsettings`

### Step 4 — Commit
`git add -A && git commit -m "fix: integration sweep for #{{ workflow.input.epic_id }}"`
