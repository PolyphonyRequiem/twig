---
id: 1003
title: What is the 1.0 TUI?
type: grilling
status: open
blocked_by: []
---

## Question

What does a finished 1.0 TUI actually do?

The scope call is banked — the TUI is committed to 1.0, needs "a lot of real work", and its
current ~774 lines across 3 files (`Program.cs` 149, `TreeNavigatorView.cs` 278,
`WorkItemFormView.cs` 347) are a **starting point, not a deliverable**. But nobody has said
what "finished" means, and this is the largest unspecified area on the map.

**HITL.** This cannot be resolved by an agent alone — it is a product question, and the
answer is the owner's.

## Why this is not blocked by the fold

[Fold the TUI into one binary](1002-fold-the-tui-into-one-binary.md) is packaging; this is
product. They are independent: the fold does not care what the TUI does, and this does not
care where the binary lives. Running this ticket first is fine and probably better — it
tells the fold how much surface it is folding.

## Where the answer will come from

Do not start from the code. Start from what the owner wants to *do* in a terminal that the
CLI makes awkward — the TUI exists because some interactions are worse as one-shot
commands, and naming those interactions is the actual question. The existing two views
(tree navigation, work-item form) are evidence of an earlier answer, not a constraint on
this one.

Relevant settled context to bring, not re-derive:
- The TUI is a CLI *concept* — same user, same terminal, same mental model (owner,
  2026-07-26) — but was explicitly allowed to "be its own product."
- The architecture map's 0002 ruled three surfaces sit at one **capability** seam, and that
  the axis that actually varies is **interactivity**. The TUI is the most interactive
  surface twig has, so it is the one that seam was shaped for.
- The architecture map's 0006 found the TUI stages edits exactly as the CLI does
  (`WorkItemFormView.cs:240` mirrors `EditCommand.cs:203-210`), so it already shares the
  pending-set model rather than having its own.

## Expected output

Almost certainly not one answer. This ticket likely graduates into several — design, UX
mockup, execute — and its own job is to make those phraseable. Resolving it means the next
tickets can be written sharply, not that the TUI is designed.

## Answer

<!-- empty until resolved -->
