File the closeout observations and improvements as a reviewable ADO Issue.

**Epic:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Observations:** {{ close_out.output.observations }}
**Improvements:** {{ close_out.output.improvements | json }}
**Agent Struggles:** {{ close_out.output.agent_struggles | json }}

## Steps

1. **Create a new Issue via twig seed:**
   - `twig set {{ intake.output.epic_id }} --output json` (set context to the completed epic)
   - `twig seed new --type Issue --title "{{ intake.output.epic_title }} — Closeout Notes"`
   - `twig seed publish --all`

2. **Tag the Issue for discovery:**
   - `twig set <new_issue_id> --output json`
   - `twig update System.Tags "closeout-notes; Needs Review"`

3. **Add a rich markdown description:**
   - `twig update System.Description "<description>" --format markdown`

   The description MUST include these sections:

   ```markdown
   # Closeout Notes: <Epic Title>

   **Source Epic:** #<epic_id>
   **Filed automatically** by the SDLC closeout workflow.

   ## Observations

   <The full observations text — what went well, cadence, accuracy, deviations>

   ## Improvement Suggestions

   <For each improvement, a numbered item with the full text>

   1. **<short title>** — <full improvement text>
   2. **<short title>** — <full improvement text>
   ...

   ## Agent Struggles

   <If any agent_struggles were reported, list them here. Otherwise omit this section.>

   ---
   *Review these findings and create follow-up work items for any improvements worth pursuing.
   Remove the "Needs Review" tag after triage.*
   ```

4. **Assign to the user:**
   - `twig update System.AssignedTo "Daniel Green"`

5. **Verify:**
   - `twig status --output json` — confirm the Issue was created with correct tags

## Output

- `issue_id`: The newly created Issue ID
- `issue_title`: The Issue title
- `summary`: A one-line summary (e.g., "Filed 5 improvements from Epic #1519 closeout as Issue #1720")
