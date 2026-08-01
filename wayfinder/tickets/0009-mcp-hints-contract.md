---
id: 0009
title: MCP hints contract
type: grilling
status: open
blocked_by: [0002]
parked: false
unparked: 2026-07-31
unparked_reason: >
  UNPARKED (2026-07-31). Both reasons that held this ticket are now spent.

  The 0012 freeze lifted at the 1.0 map (0012's own second clause). And the
  MCP map this ticket was reassigned to was NEVER CHARTERED — Daniel reframed
  the question first ("what WOULD be worthwhile to put into an MCP? If
  anything"), and 0020 answered it in one session: nothing new. A map to
  design a surface that should not grow is ceremony, so 0020 and 0021 live on
  the architecture map and THIS TICKET STAYS HERE. Do not move it.

  Its upstream question — "what should MCP expose" — is answered: the 41-tool
  surface is the ceiling and the answer, minus the three tools 0021 deletes.
  The hint contract is now a well-posed question against a settled surface.

  SEQUENCING: resolve AFTER 0021. That ticket changes tool signatures and
  deletes three tools, so specifying a hint contract before it lands would
  specify hints for a surface that is about to change shape.

  One fact from 0020 that bears on this ticket: MCP read payloads carry no
  `as_of` field, and 0012's cache guidance recommended one so a model can
  report staleness honestly. That is an envelope-level field and plausibly
  belongs to whatever this ticket decides the envelope contract is.
---

## Question

What is the AI-facing `hints` contract, and where does it live? It is currently split across two mechanisms with contradictory behaviour: `EnvelopeBuilder.cs:255` always writes a `hints` key, while `McpHintProvider.ApplyHintsAsync` at `McpHintProvider.cs:64` returns the result untouched when `verbose=false`, emitting no key at all. The audit found `ApplyHintsAsync` has ZERO production callers — the contradictory contract is pinned only by tests. Separately, "hint" names three unrelated concepts in the codebase, reproducing the `Workspace` failure mode from `CONTEXT.md` §4 in the output layer. Decide the single concept and its interface; the naming follows.

## PARKED (0012, 2026-07-26)

The MCP surface is FROZEN — no new tools, no parity work (see 0012). Specifying a hint
contract for a surface under a build-freeze is premature. Park this until the freeze lifts,
then resolve it against whatever tool shape exists at that point.

## Update (0002, 2026-07-26)

The owner has positioned MCP as an **LLM toolkit rather than a CLI proxy** — a scripting
interface (`twig_batch`) plus high-level intent tools (`twig_find_or_create`), driving
twig operations itself rather than mirroring commands one-for-one.

That changes what a "hints contract" is for. Hints designed to help an LLM drive
per-command proxies are a different artifact from hints attached to intent-level tools,
where much of the guidance is already encoded IN the tool. Resolve 0002's toolkit
scenarios before specifying the hint contract, or it will be specified against the wrong
tool shape.

## Answer

<!-- empty until resolved -->
