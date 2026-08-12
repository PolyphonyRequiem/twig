---
id: 1004
title: Read the ADO work item form layout, and export it to disk
type: execute
status: open
blocked_by: []
tracked_in: [242]
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

---

## Ruling — does `twig process layout` survive its overlap with `twig process description`? (AB#242)

**Status: MEASURED. The decision itself is Daniel's and is recorded below once made.**

`docs/specs/process-description.spec.md` (branch `docs/process-descriptor-map`) carried exactly
one open question, deferred to the build by Daniel on the grounds that the overlap should be
**observed rather than predicted**. It is now observable. Everything in this section is from real
runs against the live `Niflheim` process (14 types, org `PolyphonyRequiem`, project `Twig`), not
from reading the code.

### How it was measured

```bash
twig process description --out desc.json -o json          # whole process, 14 types
twig process layout <display-name> --out lay.json -o json # once per type, 14 times
```

Both outputs were then compared structurally: every layout control was reduced to its
`(page, section, group, controlId)` tuple in both documents and the sets and sequences compared.

### 1. The data overlap is TOTAL, and it is a strict superset

| | `process layout` (14 runs) | `process description` (1 run) |
|---|---|---|
| Field controls emitted | **117** | **117** |
| Same control set, per type | — | **identical, 11/11 servable types** |
| Same control ORDER, per type | — | **identical, 11/11** |
| Page ids identical | — | **11/11** |
| Group ids identical | — | **11/11** |
| System controls emitted | **0** | **99** (9 per type) |

🔴 **The description emits every control the layout command emits, in the same order, and 99 rows
it does not.** There is no control, page or group the layout command reports that the description
omits. The overlap is not partial.

Per-row attributes, comparing the two documents' own key sets:

| Row | Attributes in `layout` | Extra attributes in `description` |
|---|---|---|
| page | 6 | `inherited`, `order` |
| group | 5 | `inherited`, `order`, `section` |
| control | 7 | `inherited`, `order`, `section` |

The layout command carries **no attribute the description lacks**. `section` survives in the
layout command only as a nesting level rather than a named value; the description carries it as
an explicit key on each row.

### 2. Where the two genuinely differ — three real differences, not one

**a. Type addressing is inconsistent between the two verbs.** `process layout` takes a **display
name** (`Task`); `process description` takes a **reference name** (`Niflheim.Task`).
`twig process description Task` fails with *"Work item type 'Task' does not exist in this
process"*. This is a live inconsistency in the same command family and is worth fixing whichever
way #242 goes.

**b. Locked system types.** Three of the 14 (`TestCase`, `TestPlan`, `TestSuite`) are locked and
the layout route answers **400 VS403115**, not 404.

- `process layout "Test Case"` → **exit 1**, raw server error, no output.
- `process description` → those types appear with `unfetched: formLayout` and the document still
  carries the other 11. **The description degrades; the layout command fails.**

**c. Shape and audience.** `layout` emits a nested tree plus a readable indented human rendering
of the form (~22 lines for Task). The description emits **flat** rows carrying their full path,
and its human rendering is the deliberately-abridged one-line-per-type summary — it prints **no
layout detail at all**. Reading one type's form in a terminal is served by `layout` today and by
nothing else.

### 3. Code overlap is SMALLER than the data overlap suggests

Non-comment, non-blank lines:

| Component | Lines | Shared? |
|---|---:|---|
| Wire DTO `AdoFormLayoutResponse.cs` | 73 | ✅ **shared by both paths** |
| Route + pinned api-version `AdoApiVersions.ProcessLayout = "7.1"` | 1 | ✅ **shared constant, two call sites** |
| `FormLayout.cs` (layout's value object) | 31 | layout only |
| `ProcessDescriptionLayout.cs` (description's value object) | 33 | description only |
| Fetch + map, `AdoIterationService` | 122 | layout only |
| Fetch + map, `AdoProcessDescriptionSource` | 70 | description only |
| Render, `ProcessLayoutCommand.BuildLayoutTree` | 82 | layout only |
| Render, layout block in `ProcessDescriptionDocument` | 76 | description only |
| `ProcessLayoutCommand` shell (validation, `--out`, errors) | 55 | layout only |

**Shared: 74 lines (the DTO and the route constant). Duplicated-in-spirit: ~190 lines of
fetch/map and ~158 lines of render.** So the duplication is real but it is **parallel
implementation over a shared wire contract**, not copy-paste — and `ProcessDescriptionLayout`'s
own remarks already record why the split exists: `FormLayout` does **not carry the server's
`order` key**, and the description cannot be byte-stable without it. Adding `order` to
`FormLayout` would change a shipped **public** record's constructor.

🔴 **`FormLayout` is not the layout command's private type.** It has **15** referencing files, and
three of them are the TUI (`DetailDocumentSource`, `WorkItemFormView`, `Program`) plus
`WorkItemDetailProjector` and `FallbackFormLayout`. Deleting the layout *command* frees the
command shell and its renderer — **~137 lines** — and nothing else. The fetch path is production
code for the 1.0 server-driven editor and ships regardless; this ticket says so at the top.

### 4. Cost of the overlap, measured

| Invocation | Wall time (3 runs) | Bytes |
|---|---|---|
| `process layout Task` | 1.29 / 1.35 / 1.53 s | 8,360 |
| `process description Niflheim.Task` | 1.92 / 1.81 / 1.75 s | 50,133 |
| `process description` (whole, 14 types) | 2.94 / 2.86 / 2.99 s | 508,793 |

Reading one type's form via the description costs **~0.4 s more and 6× the bytes**, and the human
rendering of it carries **no layout detail whatsoever**.

Test surface: `ProcessLayoutCommandTests` (345 lines) + `ProcessLayoutSampleExportTests`
(209 lines) = **554 lines** attributable to the command.

### The two shapes

**Shape A — `layout` survives as its own command, and the inconsistencies are fixed.**
Keep both verbs. Treat the ~137 duplicated command-and-render lines as the accepted cost the
separate-verb ruling already priced in, and spend a small follow-up on the three measured
differences: accept a reference name as well as a display name, and stop failing hard on locked
types (report them the way the description does).

- *For:* the layout command is the **only** surface that renders a readable form to a terminal —
  the description's human rendering is abridged by binding ruling and shows none of it, and
  Decision 10 explicitly **forbids** per-part selection that would let the description serve
  "just the layout". It is 6× cheaper for the one-type case, it is the input the 1.0 editor work
  was built around, and it is `internal` so nothing public is frozen by keeping it.
- *Against:* two renderers over one wire payload stay in the tree, and can drift.

**Shape B — `layout` becomes a view onto the description.**
Delete the command's own fetch/render path and have `process layout <type>` render the layout rows
out of the assembled description document.

- *For:* one fetch path, one ordering authority, ~137 lines and one renderer gone; `order` and
  `inherited` arrive at the layout surface for free.
- *Against:* it makes the cheap one-type read pay the description's assembly cost; the
  description's layout rows are **flat and path-prefixed**, so the readable indented rendering has
  to be rebuilt from them anyway (the ~82 lines come back in a different file); and it couples a
  1.0-editor-adjacent command to a `0.1` document whose own spec says the layout shape is still
  under design. It also brushes against Decision 10 — a layout-only view *is* per-part selection,
  even if it is a separate command rather than a switch.

### Recommendation

🔴 **Shape A, ranked first, and not narrowly.** The overlap that was feared is a *data* overlap and
it is total; the overlap that actually costs anything is ~137 lines of command-and-render code
over a **shared** DTO and route. Against that, `layout` is the only surface that renders a form a
person can read, and the ruling that made the description's human rendering abridged is the same
ruling that stops the description ever replacing it. Shape B pays real coupling for a saving that
mostly reappears elsewhere.

The honest tidy-up is not a merge — it is the **three measured differences** in §2, which are
worth their own ticket regardless of which shape is chosen.

**Ruled by:** _pending — Daniel._
