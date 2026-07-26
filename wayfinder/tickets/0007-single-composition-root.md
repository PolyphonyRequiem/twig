---
id: 0007
title: Single composition root
type: grilling
status: open
blocked_by: [0002]
---

## Question

Can the three composition roots become one shared registration module? The audit found the obstacle is CARDINALITY, not DI-versus-manual: the CLI has one workspace per process, MCP has N keyed by `WorkspaceKey`, and the TUI has its own third root. Proposed shape: one shared `AddWorkspaceServices(IServiceCollection)` called into the CLI root container and into a per-`WorkspaceKey` child provider in MCP, with `WorkspaceContext` shrinking from a 33-parameter bundle to a thin accessor. Deletion test on `WorkspaceContextFactory.CreateContext`: FAIL — it is a manual mirror of CLI DI, as its own doc comment admits. That mirror is the mechanism behind #269 and #270.

## Update (0002, 2026-07-26) — the TUI root is now a defect, not a variant

The owner defined the TUI as *"rich UI sessions from the CLI"* — a MODE OF THE CLI, not a
separate application. That removes the third root from the cardinality argument entirely:
the CLI and TUI are the same process with the same one-Connection-per-process shape, so
`src/Twig.Tui`'s separate composition root has **no cardinality justification**. It is
duplication.

This narrows the ticket. Only MCP's N-per-process keying is a genuine cardinality
difference; the CLI/TUI split is a merge, not a design question. Two roots to reconcile,
not three, and one of the two is unambiguous.

Note also that the `Workspace` vocabulary is retired (0001 §6): `AddWorkspaceServices`
should be `AddConnectionServices`, and `WorkspaceKey` becomes `Connection`.

## Answer

<!-- empty until resolved -->
