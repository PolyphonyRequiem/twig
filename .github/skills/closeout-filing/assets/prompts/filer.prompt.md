File closeout findings as ADO work items based on the scanner's filing plan.

**Completed Epic:** #{{ workflow.input.epic_id }} — {{ scanner.output.epic_title }}
**Observations:** {{ workflow.input.observations }}
**Filing Plan:** {{ scanner.output.filing_plan | json }}
**Existing Closeout Issue:** {{ scanner.output.existing_issue_id | default("none") }}
**New Findings to File:** {{ scanner.output.new_count }}
**Duplicates Skipped:** {{ scanner.output.duplicate_count }}

{% if scanner.output.new_count == 0 %}
## No New Findings

All improvements from this closeout are already tracked as existing tasks.
Skip to the verification step and report the deduplication summary.

{% else %}
## Step 1: Create or Update the Closeout Issue

{% if scanner.output.existing_issue_id %}
A closeout Issue already exists: #{{ scanner.output.existing_issue_id }}

Update its description to include the latest observations:
- `twig set {{ scanner.output.existing_issue_id }} --output json`
- `twig update System.Description "<updated description with new observations>" --format markdown`

The description should include:
- The original observations text
- A note that this is an updated closeout (mention the new findings being added)
- A summary of all tracked improvements (existing + new)

{% else %}
Create a new closeout Issue under Epic #1603:
- `twig set 1603 --output json`
- `twig seed new --type Issue --title "{{ scanner.output.epic_title }} (Epic #{{ workflow.input.epic_id }}) Closeout"`

Then publish:
- `twig seed publish --all`

After publishing, update the Issue description:
- `twig set <new_issue_id> --output json`
- `twig update System.Description "<description>" --format markdown`

The description should include:
- **Observations**: The full observations text from the closeout
- **Scope**: How many new findings vs duplicates
- **Context**: Link back to the original epic
{% endif %}

## Step 2: Create Tasks for New Findings

For each item in the filing plan with status "new":

1. Set the parent context:
   - `twig set <closeout_issue_id> --output json`

2. Create the task:
   - `twig seed new --type Task --title "<suggested_title from filing plan>"`

3. After creating ALL tasks, publish them all at once:
   - `twig seed publish --all`

4. For each newly created task, add a rich description:
   - `twig set <task_id> --output json`
   - `twig update System.Description "<description>" --format markdown`

   Each task description MUST include:
   - **What**: What the improvement is (from the original improvement text)
   - **Why**: Why this matters (infer from the context and observations)
   - **Acceptance Criteria**: What "done" looks like for this improvement
   - **Origin**: "Filed from closeout of Epic #{{ workflow.input.epic_id }} ({{ scanner.output.epic_title }})"

   Use markdown headings, bullet lists, bold, and code formatting for readability.
   Write at least 2-3 paragraphs — these descriptions should be professional and actionable.

## Step 3: Verify

After all items are created:
- `twig set <closeout_issue_id> --output json`
- `twig tree --output json`
- Confirm all expected Tasks appear under the Issue
- Confirm the count matches `new_count` from the filing plan

{% endif %}

## Output

Report your results:
- `issue_id`: The closeout Issue ID (existing or newly created)
- `tasks_created`: Array of {id, title} for each new task
- `tasks_skipped`: Array of {improvement, duplicate_of} for each skipped duplicate
- `summary`: Human-readable summary of what was filed
