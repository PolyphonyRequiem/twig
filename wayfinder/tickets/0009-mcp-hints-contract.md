---
id: 0009
title: MCP hints contract
type: grilling
status: open
blocked_by: [0002]
parked: true
parked_reason: >
  0012 froze the MCP surface (2026-07-26) — 41 tools, no new tools, no parity
  work, new agent capability goes through the script CLI first. Specifying a
  hint contract for a frozen surface is premature. Per 0012 the freeze lifts on
  a demonstrated script-CLI gap OR at the 1.0 map (#282), so this ticket's fate
  is an OUTPUT of chartering 1.0, not an input to it. Do NOT claim it as a
  frontier ticket: 0002 is closed, so the blocked_by edge no longer holds it
  back and only this flag does.
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
