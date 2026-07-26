---
id: 0009
title: MCP hints contract
type: grilling
status: open
blocked_by: [0002]
---

## Question

What is the AI-facing `hints` contract, and where does it live? It is currently split across two mechanisms with contradictory behaviour: `EnvelopeBuilder.cs:255` always writes a `hints` key, while `McpHintProvider.ApplyHintsAsync` at `McpHintProvider.cs:64` returns the result untouched when `verbose=false`, emitting no key at all. The audit found `ApplyHintsAsync` has ZERO production callers — the contradictory contract is pinned only by tests. Separately, "hint" names three unrelated concepts in the codebase, reproducing the `Workspace` failure mode from `CONTEXT.md` §4 in the output layer. Decide the single concept and its interface; the naming follows.

## Answer

<!-- empty until resolved -->
