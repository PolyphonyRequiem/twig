---
id: 0007
title: Single composition root
type: grilling
status: open
blocked_by: [0002]
---

## Question

Can the three composition roots become one shared registration module? The audit found the obstacle is CARDINALITY, not DI-versus-manual: the CLI has one workspace per process, MCP has N keyed by `WorkspaceKey`, and the TUI has its own third root. Proposed shape: one shared `AddWorkspaceServices(IServiceCollection)` called into the CLI root container and into a per-`WorkspaceKey` child provider in MCP, with `WorkspaceContext` shrinking from a 33-parameter bundle to a thin accessor. Deletion test on `WorkspaceContextFactory.CreateContext`: FAIL — it is a manual mirror of CLI DI, as its own doc comment admits. That mirror is the mechanism behind #269 and #270.

## Update (0002, 2026-07-26)

The owner placed the TUI *conceptually* with the CLI — *"I think of the TUI as a CLI
concept. It can be its own product though."* Same user, same terminal, same mental model;
**packaging left open**.

That does not by itself resolve the third composition root. If the TUI ships as its own
product, a separate root may be justified; if it is a mode of one binary, it is
duplication. The cardinality argument is unchanged for MCP (N per process, keyed) and now
**depends on the packaging decision for the TUI** — so this ticket cannot fully resolve
before 0002 settles that.

Note also
should be `AddConnectionServices`, and `WorkspaceKey` becomes `Connection`.

## Answer

<!-- empty until resolved -->
