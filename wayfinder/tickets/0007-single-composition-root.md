---
id: 0007
title: Single composition root
type: grilling
status: open
blocked_by: [0002]
---

## Question

Can the three composition roots become one shared registration module? The audit found the obstacle is CARDINALITY, not DI-versus-manual: the CLI has one workspace per process, MCP has N keyed by `WorkspaceKey`, and the TUI has its own third root. Proposed shape: one shared `AddWorkspaceServices(IServiceCollection)` called into the CLI root container and into a per-`WorkspaceKey` child provider in MCP, with `WorkspaceContext` shrinking from a 33-parameter bundle to a thin accessor. Deletion test on `WorkspaceContextFactory.CreateContext`: FAIL — it is a manual mirror of CLI DI, as its own doc comment admits. That mirror is the mechanism behind #269 and #270.

## Answer

<!-- empty until resolved -->
