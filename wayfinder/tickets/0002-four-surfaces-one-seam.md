---
id: 0002
title: Four surfaces, one seam?
type: grilling
status: open
blocked_by: [0001]
---

## Question

Are human (CLI), AI (MCP), toolchain (JSON/text), and TUI four adapters at ONE seam, or genuinely different products? Evidence for one seam: the CLI already has `Twig.RenderTree` with `IRenderer`, 4 adapters, and a `RenderAudience` concept — but neither `Twig.Mcp.csproj` nor `Twig.Tui.csproj` references it, so both hand-rolled their own output stacks. Four adapters would make this emphatically a real seam rather than a hypothetical one. What differs per surface (hints? interpretation? truncation? stability guarantees? error shape?) and what is genuinely common? Decide the seam's location and its interface before any code moves.

## Answer

<!-- empty until resolved -->
