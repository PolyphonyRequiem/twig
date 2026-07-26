---
id: 0009
title: MCP hints contract
type: grilling
status: open
blocked_by: [0002]
---

## Question

What is the AI-facing `hints` contract, and where does it live? It is currently split across two mechanisms with contradictory behaviour: `EnvelopeBuilder.cs:255` always writes a `hints` key, while `McpHintProvider.ApplyHintsAsync` at `McpHintProvider.cs:64` returns the result untouched when `verbose=false`, emitting no key at all. The audit found `ApplyHintsAsync` has ZERO production callers — the contradictory contract is pinned only by tests. Separately, "hint" names three unrelated concepts in the codebase, reproducing the `Workspace` failure mode from `CONTEXT.md` §4 in the output layer. Decide the single concept and its interface; the naming follows.

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
