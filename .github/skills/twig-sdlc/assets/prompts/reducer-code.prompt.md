Review and simplify the implementation for task #{{ task_manager.output.current_task_id }}.
**Files modified:**
{% for file in coder.output.files_modified %}
- {{ file }}
{% endfor %}
**Changes:** {{ coder.output.changes_summary }}

## Reduction Tasks

1. **Survey the Implementation**
   - Read all files that were created or modified for this task
   - Assess the balance: is complexity or volume the bigger problem?
   - Identify abstraction layers, duplication, and dead code introduced

2. **Identify Reduction Opportunities**
   Evaluate each opportunity against the category table:

   | Category | When to Apply |
   |----------|---------------|
   | Dead code removal | Unused imports, unreachable paths, vestigial scaffolding |
   | Duplication merge | Near-duplicate logic that can consolidate |
   | Abstraction collapse | Pass-through wrappers, single-impl interfaces, unnecessary layers |
   | Dependency removal | Unused packages, redundant references |
   | Interface narrowing | Over-broad public APIs, unused parameters |
   | Pattern de-escalation | Factory with one product, strategy with one strategy |

3. **Execute Reductions**
   For each opportunity, in priority order:
   a. State what will be removed/simplified and the expected impact
   b. Apply the change — prefer deletion over refactoring
   c. Run tests to verify nothing breaks: `dotnet test --settings test.runsettings`
   d. Fold changes into the coder's last commit: `git add -A && git commit --amend --no-edit`
   e. Add note: `twig note --text "Reducer: <what was simplified>"`

   If a test fails after a reduction:
   - If the test was testing deleted functionality, remove the test
   - If the reduction introduced a regression, revert and skip
   - Do NOT suppress or weaken tests

If the code is already lean, report no changes and move on.
