---
id: 1004
title: Read the ADO work item form layout, and export it to disk
type: execute
status: open
blocked_by: []
---

## Why

[What is the 1.0 TUI?](1003-what-is-the-1-0-tui.md) banked a scope call: the TUI's editor
is **driven by the server's work item form layout**, in 1.0. ADO exposes the form's tabs
(pages), boxes (groups), and ordered fields (controls) per work item type, under the
process the project uses.

Two consequences, and only one of them is throwaway:

1. **Fetching and parsing the layout is production code.** If the editor is server-driven,
   twig reads layouts at runtime. That half ships regardless.
2. **Writing it to disk is the thin part** — but it is what unblocks design work. The
   owner's real layout lives behind a work data boundary and can only be pulled from a
   sandbox. An export command lets him run it there, review exactly what leaves, and hand
   over a structural file with no work item content in it.

So this is not scaffolding for a mockup. It is the first slice of the editor, with a
small command on top.

## Scope

- A layout capability: fetch the form layout for a given work item type on the current
  Connection, and parse it into a domain shape (pages → groups → controls, ordered, with
  labels, field reference names, and visibility).
- A command that writes that to a file.
- Both experiences, per the map's standing rule — the export is inherently script-shaped,
  so do not let it become interactive-only or rich-output-only.

## Open questions for whoever runs it

- **Do stock (system) processes return a layout, or only inherited/customized ones?**
  Reported to work for inherited processes; unverified for out-of-the-box ones. If stock
  processes refuse, that is a real constraint on the server-driven editor decision and must
  come back to 1003, not be worked around quietly.
- Whether the export writes one work item type or all of them. One is enough for the
  design work that motivated it.
- Control kinds that have no terminal equivalent (rich text, links grids, attachments,
  history) are **not** this ticket's problem — but the parse must preserve enough to know
  what kind each control is, or the renderer cannot decide later.

## Not in scope

- Rendering. That is a separate ticket, and its input is this ticket's output.
- Any work item values. This reads structure only.
