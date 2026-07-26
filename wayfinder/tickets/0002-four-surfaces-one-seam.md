---
id: 0002
title: Four surfaces, one seam?
type: grilling
status: open
blocked_by: [0001]
---

## Question

Are human (CLI), AI (MCP), toolchain (JSON/text), and TUI four adapters at ONE seam, or genuinely different products? Evidence for one seam: the CLI already has `Twig.RenderTree` with `IRenderer`, 4 adapters, and a `RenderAudience` concept — but neither `Twig.Mcp.csproj` nor `Twig.Tui.csproj` references it, so both hand-rolled their own output stacks. Four adapters would make this emphatically a real seam rather than a hypothetical one. What differs per surface (hints? interpretation? truncation? stability guarantees? error shape?) and what is genuinely common? Decide the seam's location and its interface before any code moves.

## The four experiences (owner, 2026-07-26)

Definitions given by the owner. These supersede the loose "human/AI/toolchain/TUI"
shorthand used in the audit, which conflated *audience* with *interaction model*.

| # | Experience | Consumer | Wants |
|---|---|---|---|
| 1 | **Rich CLI** | a human at a terminal | rendered text: colour, tables, truncation, hints, interpretation |
| 2 | **Script CLI** | a script, pipe, or CI job | machine-readable **stdio AND fileio** — a stable, parseable, boring contract |
| 3 | **MCP** | an LLM | control the bench / pending set, and **answer questions about local OR remote data** |
| 4 | **TUI** | a human wanting a session | **rich UI sessions launched from the CLI**, with multiple modes and views |

Three things this framing settles or changes:

**a. The TUI is a MODE OF THE CLI, not a fourth product.** *"rich ui sessions from the
CLI"* — the user runs `twig`, and the TUI is what they enter for longer-lived work. This
contradicts the current structure: `src/Twig.Tui` has its **own composition root** and its
own output stack (`Twig.Tui.csproj` does not reference `Twig.RenderTree`). If the TUI is a
CLI mode, that third composition root is a **defect**, not a design choice — and it
strengthens ticket 0007 considerably. It also partly answers the map's open question about
whether the TUI is committed: it is committed *as a mode*, which is a smaller and cheaper
commitment than a separate application.

**b. Experience 3 has a capability the other three lack: REACH.** *"answer questions about
the local OR remote data."* The other three surfaces read what twig has cached. MCP may be
asked about data twig has never seen, and must decide whether to fetch. That is a
**capability** difference, not a rendering one — and it interacts directly with 0001 §5
(the sync boundary must be explicit and user-owned): if an LLM can trigger a fetch by
asking a question, who owns *that* boundary?

**c. Experience 2 includes FILE output, not just stdout.** *"machine readable stdio and
fileio."* The audit treated the toolchain surface as `--json` on stdout. Files written for
another program to consume are equally a contract, and nothing currently versions or
tests either. Widens 0010 (toolchain output stability).

**Sharpest axis so far — interactivity (from 0001 §3d).** 1 and 4 can be *asked a
question*; 2 cannot; 3 can, but only through the LLM as intermediary. Conflict resolution
must therefore branch on this, not on output format. This may be a better seam boundary
than rendering.

**Open:** are 1 and 4 the same surface at different session lengths (both rendered, both
interactive, differing only in whether state persists across commands)? If so there may be
**three** surfaces and two *presentation modes*, not four surfaces.

## Answer

<!-- empty until resolved -->
