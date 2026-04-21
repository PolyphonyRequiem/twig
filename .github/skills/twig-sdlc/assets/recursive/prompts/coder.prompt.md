Implement the following task.
**Task:** #{{ workflow.input.task_id }} — {{ workflow.input.task_title }}
**Description:** {{ workflow.input.task_description }}
**Issue:** #{{ workflow.input.issue_id }} — {{ workflow.input.issue_title }}
**Branch:** {{ workflow.input.branch_name }}
**Plan:** Read `{{ workflow.input.plan_path }}` for full context.
{% if task_reviewer is defined and task_reviewer.output and not task_reviewer.output.approved %}
**Previous review — fix these issues:**
{{ task_reviewer.output.feedback | default('') }}
{% for issue in task_reviewer.output.issues %}
- {{ issue }}
{% endfor %}
{% endif %}

## Steps

### Step 0 — Prior State Check (< 3 minutes, MANDATORY)
Before doing ANY research or implementation, check if this task was already worked on:
```
git --no-pager log --oneline -10
git --no-pager diff --stat HEAD
git --no-pager status --short
```
**If commits already exist for this task** (matching the task ID, issue, or description):
- Verify the existing work is correct with *targeted* spot-checks — NOT a full re-verification
- If it looks good: run the scope-appropriate tests (see **Step 4**), commit any uncommitted changes, and go straight to **Output**
- If it has problems: fix only what's broken, don't redo from scratch
- **Budget: spend ≤ 5 minutes verifying prior work. Trust prior commits unless you find concrete evidence of breakage.**

**If no prior work exists**, proceed to Step 1.

### Step 1 — Targeted Research (< 10 minutes)
Research ONLY what you need for THIS task — not the whole codebase:
- Read the plan file for this task's specific section
- Identify files to create/modify (use `grep` and `glob`, not exploratory shell commands)
- Check the conventions of 1-2 similar existing files as reference
- Add a twig note: `twig note --text "Research: <findings>"`

Do NOT: enumerate all modules, review every interface, or catalog the entire codebase.

### Step 2 — Implement
Implement the changes following existing conventions.
- Add a twig note: `twig note --text "Impl: <what was done>"`

### Step 3 — Write Tests
Write tests covering the new functionality and edge cases.
- Track edge cases you explicitly handled (for reviewer visibility)

### Step 4 — Run Tests (scope-aware)
Pick the narrowest suite that covers your changes:
- **Changes confined to `ext/twig-vscode/**`** → `cd ext/twig-vscode && npm run compile && npm run test:unit`
- **Otherwise (.NET changes)** → `dotnet build` once, then `dotnet test --settings test.runsettings --no-restore --no-build`
- **Mixed** → run both suites.

Add a twig note: `twig note --text "Tests: <count> passed"`

**Shell watchdog.** If a test/build shell produces zero output after ~3 consecutive
`read_powershell` polls (~6 minutes), `stop_powershell` and narrow scope or retry.

### Step 5 — Commit
`git add -A && git commit -m "<descriptive message>"`

Do NOT implement anything beyond this single task.

## Pre-Review Checklist (avoid review round-trips)
Before committing, self-check against these criteria that the reviewer will enforce:
- [ ] Requirements met — implementation satisfies the task description
- [ ] Code quality — clean, idiomatic C#, follows project conventions (sealed classes, primary constructors)
- [ ] AOT compliance — no reflection, all JSON uses TwigJsonContext, no dynamic loading
- [ ] Test coverage — edge cases covered, tests verify behavior not implementation
- [ ] No stale references — renamed/removed methods updated at ALL call sites
- [ ] Builds clean — `dotnet build` produces zero warnings (TreatWarningsAsErrors)
- [ ] All tests pass — scope-appropriate suite from **Step 4** is green
