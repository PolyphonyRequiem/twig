---
id: 0008
title: Registration completeness tests
type: task
status: open
---

## Question

Add completeness tests across all six registration touch points, so a missing registration fails the build instead of silently breaking a capability. MCP has three touch points (`AddSingleton<XTools>` at `Twig.Mcp/Program.cs:65-75`, `.WithTools<X>()` at `:107-118`, `AllToolNames` at `McpToolCatalog.cs:22-65`) and the code comments the trap on itself at `Program.cs:63-64`. The CLI has three (handler method, `CommandRegistrationModule.cs:36-107`, `GroupedHelp.KnownCommands` at `Program.cs:1180-1293`) and only the third currently has a test guard. The invariant holds today (41=41, 40=40, no orphans) BY HAND ONLY. This is cheap, independent of every other ticket, and kills the whole footgun class without any refactor — which is why it has no blockers and can be taken at any time.

## Answer

<!-- empty until resolved -->
