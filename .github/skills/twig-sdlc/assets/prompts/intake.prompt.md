Gather context for the SDLC workflow.
{% if workflow.input.work_item_id %}
**Existing work item:** #{{ workflow.input.work_item_id }}
1. Run: `twig set {{ workflow.input.work_item_id }} --output json`
2. Run: `twig status --output json` to get full details
3. Run: `twig tree --output json` to see any existing child items
4. Read the work item's description, acceptance criteria, and any linked items
5. Check the item's type (Epic or Issue) and current state
If the item already has child Issues with descriptions, include them in existing_issues.
{% endif %}
{% if workflow.input.prompt %}
**New work request:** {{ workflow.input.prompt }}
1. Create a new Epic via twig:
   - `twig set 1 --output json` (set to the root Epic)
   - `twig seed new --title "<derived title>" --type Epic --output json`
   - `twig seed publish --all --output json`
2. Wait 3 seconds, refresh, then set context to the new item
3. Assign and describe it:
   - `twig update System.AssignedTo "Daniel Green"`
   - `twig update System.Description "<rich markdown with headings, context, acceptance criteria>" --format markdown`
   Write a comprehensive description (2+ paragraphs) with `## What`, `## Why`, `## Acceptance Criteria` headings.
{% endif %}
## Plan Detection
{% if workflow.input.plan_path %}
A plan file was explicitly provided: `{{ workflow.input.plan_path }}`
Read the file and check its `> **Status**:` header line.
If the status contains "Approved", "In Progress", or "Done",
set `plan_already_approved` to true and `existing_plan_path` to the path.
{% else %}
After gathering work item details, search for an existing plan file:
1. Look for `docs/projects/*.plan.md` files that reference this epic's ID
   (e.g., grep for `#{{ workflow.input.work_item_id }}` in plan files)
2. If found, read the `> **Status**:` header line
3. If status contains "Approved" or "In Progress", the plan is ready —
   set `plan_already_approved` to true and `existing_plan_path` to the path
4. If no approved plan exists, set `plan_already_approved` to false
{% endif %}
Output the work item details for the next phase.
