Detect whether another conductor SDLC run is already live for this work item,
or whether a sibling git worktree already owns the `sdlc/<id>` branch.

Run exactly one command (cross-platform via pwsh):

```
pwsh -NoProfile -File .github/skills/twig-sdlc/assets/scripts/check-duplicate-session.ps1 -WorkItemId {{ workflow.input.work_item_id | default(0) }}
```

The script prints a single-line JSON object with three keys:
`is_duplicate` (boolean), `details` (string), `reason` (string).

Copy the values verbatim into your output fields:

- `is_duplicate` ← JSON `is_duplicate`
- `details` ← JSON `details`
- `reason` ← JSON `reason`

If the script errors or produces no parseable output, set `is_duplicate=false`,
`details=""`, and put the failure message in `reason` so the workflow proceeds.
Do not attempt to work around the script or re-implement its logic.
