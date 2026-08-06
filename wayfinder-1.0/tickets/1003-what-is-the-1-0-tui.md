---
id: 1003
title: What is the 1.0 TUI?
type: grilling
status: partially-answered
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

**Status: partially answered.** Four decisions banked with the owner on 2026-08-01. The
question "what does a finished 1.0 TUI do" is not fully closed — see *Still open* — but it
is no longer the largest unspecified area on the map, and follow-on work is now phraseable.

### What the TUI is for (owner, 2026-08-01)

> "looking and setting field values and rapidly navigating the tree ... viewing query
> results and navigating them would be an example"

Four named interactions, in the owner's language: **looking**, **setting field values**,
**rapid tree navigation**, and **viewing and navigating query results**. Query results are
new — the current implementation has no such view. Note the shape difference this
introduces: a query result is a flat set, the tree is a hierarchy, and the TUI now has to
hold both.

### Many entry points, not one door

> "quite a few things could be entry points, edit could become interactive by default for
> instance"

**The TUI is not a place you go — it is what a command does when it is interactive.**
Multiple commands can open it. `edit` becoming interactive by default is the owner's own
example.

This reframes the surface. It is not "twig, plus a TUI you launch"; it is one capability
set with an interactive rendering that several commands can enter. That is consistent with
the architecture map's 0002 (three surfaces at one capability seam, with **interactivity**
as the axis that varies) — the TUI is that axis turned up, reached from wherever you
already are.

**Consequence for the fold ([1002](1002-fold-the-tui-into-one-binary.md)):** if ordinary
commands open interactively by default, the TUI cannot sensibly live in a separate binary
that the user invokes by name. This does not decide 1002 — that is gated on the Windows
AOT verification (#359) — but it removes "keep them separate, it's just a different tool"
as a *product* argument. If the split survives, it survives as a packaging compromise, not
a design.

### The editor is server-driven, in 1.0

> "the editor would ideally somewhat match the tabbed layout of the web UI editor"
> ... "it's a 1.0 thing."

ADO exposes the form layout per work item type — pages (tabs), groups (boxes), and ordered
controls (fields, with labels and visibility) — under the process the project uses. Twig
already reaches the process-scoped API area for iterations
(`AdoIterationService.cs:319`), so auth and plumbing exist; nothing new is needed to get
there.

**Banked: the 1.0 editor takes its tab and group structure from the server, not from a
hand-written layout.** The owner overrode the recommendation to spike now and hand-write
for 1.0, which was the safer call and the wrong one for this product: hand-authoring a
layout means every customer with a customized process gets a form that doesn't match
theirs.

Two caveats carried forward honestly, both unverified:

- **Stock vs inherited processes.** Layout retrieval is reported to work for inherited
  (customized) processes; whether out-of-the-box processes return a layout is unconfirmed.
  If they do not, that is a genuine constraint on this decision and comes back here.
- **Structure transfers; widgets do not.** The layout describes tabs, groups, and field
  order — all of which have terminal equivalents. It does not describe rendering, and some
  web controls (rich text, links grids, attachments, history) have no obvious terminal
  form. Server-driven layout does **not** mean server-driven widgets, and the mapping from
  control kind to terminal presentation is hand-written work that this decision does not
  eliminate.

### Getting a real layout is itself a ticket

A prototype needs a real layout document. The owner's is work data behind a sandbox
boundary, and the structural half (tab/group/field names) is the only part that needs to
cross it — no work item content is required to answer any rendering question.

Rather than hand over credentials or hack a throwaway fetcher, this graduated into
[1004](1004-export-work-item-form-layout.md): twig gains the ability to read a form layout
and write it to disk. The fetch-and-parse half is production code for the server-driven
editor regardless; only the export command is thin. The owner runs it in the sandbox,
reviews what leaves, and hands over a structural file.

### Still open

- **Session vs one-shot.** Once a command opens interactively, do you stay — navigating,
  querying, switching benches until you quit — or do you finish that one thing and return
  to the shell? Asked; overtaken by the layout thread before it was answered. This is the
  single largest remaining cost driver on the TUI, because every interaction the owner
  named except "set field values" dies if the surface exits after one item. **Ask this
  first next time.**
- **What "multiple modes and views" means** (the owner's own banked language from
  2026-07-26). Unprobed.
- **Whether the TUI is where you see and resolve staleness.** The reconciliation ruling
  (architecture 0004) already assigns the TUI the job of rendering staleness *as state*.
  Whether that makes it a reconciliation cockpit or is incidental was not reached.
- **Bench management** — a named, persistent, switchable set is exactly the thing that is
  awkward as one-shot commands. Not discussed.
- **What the TUI is NOT.** No non-goals were banked. Still cheap and still valuable.

### Not chartered: a TUI map

Considered and deliberately not done. Four decisions and one spawned execute ticket do not
justify a sibling map with its own destination and id range; the remaining open questions
above are ticket-shaped on this map. Revisit if the session-vs-one-shot answer turns out to
be "full session", which could plausibly triple the surface.
