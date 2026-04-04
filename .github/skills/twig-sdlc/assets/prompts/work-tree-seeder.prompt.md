Create the ADO work tree from the implementation plan.
**Parent:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Type:** {{ intake.output.item_type }}
**Plan:** Read `{{ (architect.output.plan_path if architect is defined and architect.output else plan_reader.output.plan_path) }}` to get the structure

## Plan Status Update (FIRST)
Update the plan file's Status line to `> **Status**: 🔨 In Progress` before creating any items.

## Duplicate Guard (MANDATORY first step)
Before creating ANY items, inventory what already exists:
1. `twig set {{ intake.output.epic_id }} --output json`
2. `twig tree --output json` to list existing children (Issues or Tasks)
3. For each existing Issue, drill in: `twig set <id>; twig tree --output json`
   to discover its child Tasks.
4. Compare each **planned** item against the existing inventory.
   Use YOUR judgment about **functional similarity** — not just exact title
   matching. Two items are duplicates if they cover the same deliverable,
   even when worded differently. Examples:
   - "Add Markdig NuGet package" ≈ "Add Markdig to central package management"
   - "Wire format through Program.cs" ≈ "Wire --format flag in CLI entrypoint"
   - "Unit tests for converter" ≈ "MarkdownConverter test coverage"
   A title that is a subset, restatement, or synonym of an existing item is
   a duplicate. When in doubt, **reuse** rather than create.
5. For each match: **reuse** the existing item — record its ID, skip creation.
6. Only create items that have NO functional match in the existing tree.

## Steps (create only missing items)
1. Read the plan file to extract the ADO structure (Issues → Tasks) and PR groups
2. Set context to the parent: `twig set {{ intake.output.epic_id }} --output json`
3. **Create missing Issues** (if input is an Epic):
   a. Create: `twig seed new --title "<issue title>" --type Issue --output json`
   b. Publish: `twig seed publish --all --output json`
   c. Wait and sync: `Start-Sleep -Seconds 3; twig sync --output json`
   d. For each Issue (new or reused), set context and add description + assignment:
      - `twig update System.Description "<markdown>" --format markdown`
      - Write rich descriptions with `## Scope`, `## Tasks`, `## Acceptance Criteria`
4. **Create missing Tasks** under each Issue (or directly under the parent if input is an Issue):
   a. `twig set <issue_id> --output json`
   b. Use `twig seed chain` for linked tasks if multiple
   c. `twig seed publish --all --output json`
   d. Wait, refresh, then update each Task with description + assignment
5. Record the full ADO structure AND the PR group assignments

**CRITICAL:** Use successor links (twig seed chain) within each Issue's Tasks
to enforce execution order. Tasks within an Issue execute sequentially.

## PR Group Mapping
PR groups are defined in the plan as a separate section from the ADO hierarchy.
Map each PR group to the set of Task IDs (and their parent Issue IDs) that
belong to it. A PR group may contain Tasks from one or multiple Issues.
Record this mapping for the implementation phase.

## ADO ↔ Plan Reconciliation (MANDATORY after seeding)

After all items are created/reused, verify full coverage:
1. `twig set {{ intake.output.epic_id }} --output json`
2. `twig tree --output json` — list ALL children in ADO
3. Compare the ADO tree against the plan's Issue list:
   - Every ADO child Issue must map to a plan Issue (or be explicitly noted as
     pre-existing/out-of-scope)
   - Every plan Issue must have a corresponding ADO child
4. If ADO has child Items NOT in the plan (e.g., added during an earlier
   workflow run or manually), include them in the work_tree output with a
   flag `"from_plan": false` so that pr_group_manager and close_out are
   aware they exist.
5. Report any discrepancies in the output — mismatched item counts are a
   red flag that the workflow may skip or orphan work items.
