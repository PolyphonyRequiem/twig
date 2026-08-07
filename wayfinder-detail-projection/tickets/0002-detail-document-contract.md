---
id: 0002
title: Define the framework-neutral detail document contract
type: grilling
status: open
claimed_by:
blocked_by: [0001]
---

## Question

What exact public document does a read-only host receive from `WorkItem + FormLayout`? Resolve identity, pages, columns, groups, fields, labels, visibility, ordering, read-only state, process-specific fields, missing values, rich/long source values, contributions, unsupported controls, and Twig-owned appearance metadata.

The contract must preserve the full source value and all server-authored structure while remaining free of renderer, workspace, persistence, and lifecycle types.

## Answer

