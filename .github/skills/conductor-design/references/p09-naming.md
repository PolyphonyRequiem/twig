# P9: Clear, Minimal Naming

Names should be clear within the scope of their workflow and node. Use as little
text as needed to be unambiguous at a glance. Avoid both cryptic abbreviations
and unnecessarily verbose descriptions.

## Guidelines

- A name is good if someone reading the workflow can understand the node's role
  without reading its prompt
- Short names are preferred when the workflow context disambiguates
  (e.g., `cleanup` is fine in a workflow that only has one cleanup step)
- Cross-boundary names (workflow inputs, sub-workflow contracts) may need more
  qualification to avoid ambiguity between contexts
- Enum values should be plain words: `new`, `redo`, `resume` — not prefixed
