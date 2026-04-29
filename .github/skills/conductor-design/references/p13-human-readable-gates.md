# P13: Human-Readable Gates

Human gates are decision points — the human needs enough context to make a good
decision quickly. Gate prompts are Jinja2 templates that render to Markdown,
displayed in both the Rich console (via `RichMarkdown`) and the web dashboard.

## P13a: Gate Templates Own Layout

Gate prompts should present information using Markdown headings, tables, bullet
lists, and emphasis — not raw JSON dumps or terse values. The gate template is
the layout layer; it pulls structured data from agent outputs and formats it for
human decision-making.

```yaml
# ❌ Don't dump JSON
prompt: |
  {{ pr_finalizer.output | json }}

# ✅ Do format for humans
prompt: |
  ## PR Verification — {{ pr_finalizer.output.summary }}

  | PG | Status |
  |----|--------|
  {% for pg in pr_finalizer.output.unmerged_pr_groups %}
  | {{ pg }} | ❌ Unmerged |
  {% endfor %}
```

## P13b: Surface Links to Relevant Artifacts

Gate prompts should include clickable links to all relevant context — plan
documents, PRs, ADO work items, source files, branches. Upstream agents should
include file paths and URLs in their output fields so gate templates can render
them as links. Conductor's `linkify_markdown()` auto-converts local file paths
into clickable links in the console.

```yaml
prompt: |
  📄 [View Plan]({{ architect.output.plan_path }})
  🔗 [ADO Work Item]({{ project_url }}/_workitems/edit/{{ intake.output.work_item_id }})
  {% if pr_submit is defined %}
  🔀 [GitHub PR]({{ pr_submit.output.pr_url }})
  {% endif %}

  ### Files Affected
  {% for file in architect.output.files_affected %}
  - [{{ file }}]({{ file }})
  {% endfor %}
```

## P13c: Agent Outputs Should Be Structured for Gate Consumption

When an agent's output feeds a human gate, prefer separate structured fields
(numbers, arrays, booleans) over monolithic strings. This lets gate templates
build tables, iterate lists, and conditionally display sections.

Narrative summaries that pass through verbatim (e.g., `progress_summary`,
`feedback`) should be Markdown-formatted by the producing agent, since the gate
template will render them as-is.
