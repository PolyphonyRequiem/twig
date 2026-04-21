Scan existing closeout findings and identify which improvements need to be filed.

**Completed Epic:** #{{ workflow.input.epic_id }}
**Observations:** {{ workflow.input.observations }}
**Improvements:** {{ workflow.input.improvements }}
**Skip Dedup:** {{ workflow.input.skip_dedup | default(false) }}

## Steps

1. **Read the completed epic's details:**
   - `twig set {{ workflow.input.epic_id }} --output json`
   - `twig status --output json`
   - Record the epic's title — you'll need it for naming the closeout Issue

2. **Read the Closeout Findings tree:**
   - `twig set 1603 --output json`
   - `twig tree --output json`
   - This shows ALL existing closeout Issues and their Tasks

3. **Check for an existing closeout Issue for this epic:**
   - Look through the children of #1603 for an Issue whose title references
     Epic #{{ workflow.input.epic_id }} or the epic's title
   - If found, record its ID — the filer will add Tasks under it rather than
     creating a new Issue
   - Also read its existing Tasks to know what's already tracked:
     `twig set <issue_id> --output json` then `twig tree --output json`

4. **Parse the improvements list:**
   - The improvements input is a JSON array of strings
   - Each string describes one improvement finding

5. **Cross-reference against existing tasks** (unless skip_dedup is true):
   - For each improvement, check ALL existing tasks under #1603 (not just
     this epic's closeout Issue)
   - A finding is a **duplicate** if an existing task covers the same core issue,
     even if the wording differs. Examples of duplicates:
     - "Add twig flush command" ≈ "Flush staged notes on sync" (same underlying issue)
     - "Gate close-out on PR completeness" ≈ "Verify all PRs merged before Epic close" (same fix)
   - A finding is **new** if no existing task addresses the same underlying problem

6. **Produce the filing plan:**
   - List each improvement as either `new` (needs filing) or `duplicate` (skip, with reference to existing task)
   - For duplicates, include the existing task ID and title that covers it

## Output Requirements

Set your output fields:
- `epic_title`: The completed epic's title (from step 1)
- `existing_issue_id`: The closeout Issue ID if one already exists, or 0 if none exists. IMPORTANT: Return the integer 0, NOT null/None.
- `existing_task_ids`: Array of existing task IDs under the closeout Issue (empty array [] if none)
- `filing_plan`: Array of objects, each with:
  - `improvement`: The original improvement text
  - `status`: "new" or "duplicate"
  - `duplicate_of_id`: Task ID if duplicate, 0 if new
  - `duplicate_of_title`: Task title if duplicate, empty string if new
  - `suggested_title`: Short actionable title for the task (if new)
- `new_count`: Number of new findings to file
- `duplicate_count`: Number of duplicates skipped
