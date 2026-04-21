Break Issue #{{ workflow.input.work_item_id }} into Tasks.

**Issue Title:** {{ workflow.input.title }}
**Issue Description:** {{ workflow.input.description }}
{% if workflow.input.plan_path %}
**Epic-level plan:** Read the plan at `{{ workflow.input.plan_path }}` for full context.
This Issue is part of a larger Epic. The plan describes what this Issue should achieve
and may list provisional Tasks. Use those as a starting point but refine based on your
own codebase research.
{% endif %}

---

## Step 1: Research

Before creating Tasks, research the specific codebase area:
- Read the files that will be modified
- Understand existing patterns and conventions
- Identify test files that need new tests
- Check for dependencies or constraints

## Step 2: Create Tasks

Create 2-5 Tasks under Issue #{{ workflow.input.work_item_id }}.
Use `twig seed chain` for efficient creation with successor links.

Each Task should be independently committable — a developer should be able to
implement one Task, commit, and have a working build.

## Step 3: Output

After creating all Tasks, output:
- `tasks`: Array of created Tasks with their ADO IDs, titles, and descriptions
- `task_count`: Number of Tasks created
- `planning_summary`: Brief summary of how you decomposed the Issue
