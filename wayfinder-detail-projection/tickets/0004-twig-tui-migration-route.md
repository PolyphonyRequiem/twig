---
id: 0004
title: Define and prove Twig TUI migration onto the shared projection
type: prototype
status: open
claimed_by:
blocked_by: [0002, 0003]
---

## Question

How does `WorkItemFormView` stop maintaining its hard-coded ten-field list and consume the same detail document without giving the shared module any Terminal.Gui or application-lifecycle responsibility?

The answer must separate read-only document painting from current editing behavior, preserve server order and process-specific fields, identify the fallback when layout data is absent, and include a narrow prototype or test that would fail if Twig TUI silently returned to a second field-selection implementation.

## Answer

