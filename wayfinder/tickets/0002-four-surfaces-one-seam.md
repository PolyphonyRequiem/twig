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

**a. The TUI is CONCEPTUALLY a CLI thing, but may still be its own product.** Owner,
2026-07-26: *"I think of the TUI as a CLI concept. It can be its own product though."*

This is a **conceptual** placement, not a packaging decision, and the two must not be
conflated (an earlier draft of this ticket did conflate them — corrected). The TUI belongs
to the CLI's world: same user, same terminal, same mental model, launched the same way.
Whether it ships as one binary or two, and whether it keeps its own composition root,
is **undecided and still in scope for this ticket and 0007**.

What that does settle: the TUI is not a *different product for a different audience* the
way MCP is. Experiences 1 and 4 serve the same person. What it does NOT settle: whether
`src/Twig.Tui`'s separate composition root and its own output stack
(`Twig.Tui.csproj` does not reference `Twig.RenderTree`) are justified. That duplication
may be right if the TUI is a separately shipped product, and wrong if it is a mode of one
binary. Both remain open.

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

**d. MCP is an LLM TOOLKIT, not a CLI proxy.** Owner, 2026-07-26:

> *"script these twig things using the scripting interface, or use more high level tools
> targeted at things the MCP might reasonably want to do, and then drive underlying twig
> operations accordingly. I see the MCP as a bit more of an LLM toolkit than just a CLI
> proxy."*

Two offered shapes, and **both already exist in the code as one-offs** — which is the real
finding here:

- **The scripting interface.** `twig_batch` (`src/Twig.Mcp/Tools/BatchTools.cs:17`) already
  accepts a JSON graph of `sequence` / `parallel` / `step` nodes, with fail-fast semantics
  and per-step `onError: continue`. That IS a scripting primitive exposed as a tool.
- **High-level intent tools.** `twig_find_or_create`
  (`src/Twig.Mcp/Tools/CreationTools.cs:120`) is not a CLI proxy: its description says
  *"Always performs a deduplication check — use this instead of twig_new when idempotent
  creation is required."* It encodes an INTENT the LLM would otherwise have to compose
  from a query plus a create, and get wrong.

The other ~39 tools are per-command proxies. So the catalogue currently holds **two
philosophies with no stated intent** — a design decision made twice by accident.

**The mechanism for selective exposure also already exists**, and is not the issue: 41
tools in `AllToolNames`, only 11 advertised by default via `CompactToolNames`
(`McpToolCatalog.cs:70`), the rest reachable with `--tool-profile full`. What is missing is
the *criterion*. The code comment calls the 11 the "high-frequency surface" — chosen by
guessing what an LLM uses often. The owner's rule is different: a tool earns its place
because a real scenario needs it.

**Consequence — "CLI ↔ MCP parity" is partly the WRONG QUESTION.** Parity assumes MCP
should mirror the CLI. If MCP is a toolkit, then some CLI commands should have no MCP tool,
and some MCP tools (composites like `find_or_create`, and `twig_batch` itself) should have
no CLI equivalent. **Divergence would be correct, not drift.** The audit's parity table
needs re-reading with that lens before it is used to justify any alignment work.

Supporting evidence that MCP-side capability has drifted from use:
`McpHintProvider.ApplyHintsAsync` has zero production callers.

**Open:** what are the scenarios? The toolkit cannot be designed until "things an LLM
might reasonably want to do" is an enumerated list rather than a phrase. That list is the
gating artifact for this half of the ticket.

**Sharpest axis so far — interactivity (from 0001 §3d).** 1 and 4 can be *asked a
question*; 2 cannot; 3 can, but only through the LLM as intermediary. Conflict resolution
must therefore branch on this, not on output format. This may be a better seam boundary
than rendering.

**Open:** are 1 and 4 the same surface at different session lengths (both rendered, both
interactive, differing only in whether state persists across commands)? If so there may be
**three** surfaces and two *presentation modes*, not four surfaces.

### The MCP freeze shrinks this ticket (0012, 2026-07-26)

MCP is **frozen** — no new tools, no parity work, script CLI first (see 0012). The seam
question therefore narrows: with one of the four experiences under a build-freeze, the live
question is **rich CLI vs script CLI vs TUI**, and the owner has already placed rich CLI and
TUI as serving the same person. MCP remains a consumer of whatever seam exists; it just
stops being a moving target while the seam is decided.

This does NOT make the ticket trivial — experience 2's contract (stdio *and* fileio, §c)
is still unversioned and untested, and that is where the investment is going.

### Who decides the seam question

The owner has **no strong feeling** on one-seam-versus-many (2026-07-26) and is not
withholding a preference. **Do not wait on him for it** — this is an engineering judgement
to be made from evidence: the deletion test, the number of real adapters, and whether the
per-surface differences (§a–d) are genuinely differences of *shaping* or of *capability*.

What he HAS given, and what the answer must respect:

- Experiences 1 and 4 serve the same person (§a).
- MCP is a selectively exposed projection of capability that exists elsewhere (§d) — which
  is itself an argument for a seam, since a projection needs a source.
- Experience 2's contract includes files, not just stdout (§c).
- Interactivity is the sharpest observed axis, not output format.

The remaining decision is therefore: **where the seam sits and what its interface is**, not
whether he wants one.

## Answer

<!-- empty until resolved -->
