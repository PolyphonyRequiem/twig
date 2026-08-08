---
id: 0002
title: Define the framework-neutral detail document contract
type: grilling
status: closed
claimed_by: detail-doc
blocked_by: [0001]
---

## Question

What exact public document does a read-only host receive from `WorkItem + FormLayout`? Resolve identity, pages, columns, groups, fields, labels, visibility, ordering, read-only state, process-specific fields, missing values, rich/long source values, contributions, unsupported controls, and Twig-owned appearance metadata.

The contract must preserve the full source value and all server-authored structure while remaining free of renderer, workspace, persistence, and lifecycle types.

## Answer

Settled with Daniel on 2026-08-07. Pinned at commit `74e2d6a14dce395cd00fa121bbd36985a3c92e04`
(`origin/main`, "docs(wayfinder): cut node-link graph rendering"), working tree clean. Every
current-code claim below was read at that commit.

### The governing rule

**Carry every fact the source gave us; let the host decide what to drop.** This is the same rule
`FormLayout`'s own remarks already state for columns — merging is a *rendering* decision, and a
projection that discards a fact leaves no way back, because the fact is gone before any renderer
gets to choose. Daniel applied it independently in this session to hidden/empty fields and to the
non-field pages, which is strong evidence it is the map's real invariant rather than a local call.

### 1. What the document is

One value bundle describing a single work item: the server's page → section → group → control
structure, with each field control's value resolved against the item, plus item identity. It is
constructed from an already-materialized `FormLayout` + `WorkItemSnapshot` (ticket 0001 §4). No
provider, no async, no store, no DI, no renderer type.

### 2. Structure — unchanged from `FormLayout`

All four ADO levels survive verbatim: pages (tabs), sections (unlabelled columns), groups
(labelled boxes), controls. Server order is authoritative, including `LayoutControl` ordering by
ADO's explicit `order` rather than array position (pinned by
`AdoIterationServiceFormLayoutTests.GetFormLayoutAsync_OrdersControlsByOrderNotArrayPosition`).
`LayoutPage.AllGroups` stays as the column-major convenience projection.

### 3. Field value states — THREE, not two

🔴 **The defect this resolves is real and would have shipped silently.** The form says "draw a
control for `System.Title`". `WorkItemSnapshot.Fields` **does not contain** `System.Title` — nor
`System.State`, `System.AssignedTo`, `System.IterationPath`, `System.AreaPath`, `System.Id`,
`System.Rev`, `System.WorkItemType`, because `FieldImportFilter.CoreFieldRefs`
(`Services/Field/FieldImportFilter.cs:12-17`) excludes all eight — they were promoted to
first-class snapshot properties. `FieldImportFilter` additionally drops **every boolean field**
(`ImportableDataTypes`, `:21-24`, with an explicit comment that the string-only dictionary cannot
represent JSON `true`/`false` faithfully) and **every server-read-only field** not on the
`DisplayWorthyReadOnlyRefs` allowlist (`:26-31`, `:43`). `AdoResponseMapper.MapToSnapshot:44` then
drops any field whose parsed value is `null`.

So a host that naively looks each control's field up in `Fields` gets **nothing** for a large,
type-dependent slice of every form — and cannot distinguish that from a genuinely blank field.

Each field control therefore resolves to exactly one of:

| State | Meaning | Host's usual move |
|---|---|---|
| **Has a value** | the value, in full | render it |
| **Empty on the server** | the item genuinely has no value here | render blank, or omit |
| **Not carried by Twig** | Twig's projection does not transport this field | render as unknown, or omit |

Two states would have been cheaper and were rejected. Daniel: *"Three I believe is more sensible."*

The eight core fields are the resolvable sub-case, not a hole: the projection reads them from
`WorkItemSnapshot`'s own properties when the control names one, so `System.Title` is
**has a value**, not **not carried**. The third state exists for the genuinely untransported
classes — booleans, filtered read-only fields, and anything the mapper dropped.

**Why not just omit the field.** Daniel's opening instinct was that omission depends on the host,
and it does — so the *document* keeps the row and the *host* omits. Two read-only cases where
silent omission is wrong: the duplicate-review pane draws two items side by side and needs rows to
align to be scannable; and a form whose Title control silently vanished looks like a form with no
title rather than a projection bug.

### 4. Non-field pages and contributions — carried, not filtered

`LayoutPage.PageType` is `custom` | `history` | `links` | `attachments`; only `custom` carries
field controls, and the other three are server-rendered surfaces whose content this layout does not
supply. `IsContribution` marks third-party add-in groups and controls that have a name and a
position but no field behind them.

**All of them stay in the document**, flagged for what they are. Daniel: *"it depends on the
configuration of the rendering host"* — which is the carry-the-fact rule again. A host that wants
only fillable content filters on the flags; a host that wants to show a disabled *History* tab, or
name an add-in it cannot draw, still can. Filtering here would be Twig deciding a host's
information architecture.

### 5. Long and rich values — full value ALWAYS, plus a short form

Description and repro-steps fields carry HTML/long text (`FieldImportFilter` admits `html` and
`plainText`). The document carries the **complete source value, never truncated** — the map's
standing constraint — **and** a Twig-computed short form for values that exceed a summary length.

Daniel: *"the renderer should be able to show both the condensed and expanded view."* The
alternative (full value only, each host cutting its own) was rejected because every host would cut
differently, and a one-line row is the common case, not the exotic one. The short form is a
convenience over the preserved value in exactly the way `AllGroups` is a convenience over
`Sections`.

### 6. Read-only and visibility — reported, never enforced

`LayoutControl.ReadOnly` and `.Visible`, and `LayoutPage`/`LayoutGroup` `.Visible`, travel as the
server set them. `visible` absent means **visible** (pinned by
`GetFormLayoutAsync_TreatsAbsentVisibleAsVisible`; defaulting it false would hide every ordinary
field and look like an empty form rather than a parse bug). The projection reports these; it does
not filter on them and it does not use `ReadOnly` to mean anything about editing — editing is
ticket 0005 and is never a construction prerequisite.

### 7. Control kinds — carried verbatim, no host-facing enum

`LayoutControl.ControlType` is preserved as the server's string (pinned by
`GetFormLayoutAsync_PreservesControlTypeAndFieldReferenceName`). The projection does **not**
translate it into a closed Twig-owned widget enum: process customization means the set is open, and
a closed enum forces every unrecognized kind into an `Other` bucket that discards the fact. A host
switches on the kinds it supports and falls back on the rest — it still has the name to log,
display, or handle later.

### 8. Identity and process

`WorkItemTypeReferenceName` and `ProcessId` from the layout (pinned by
`GetFormLayoutAsync_ReportsReferenceNameAndProcessId` — the endpoint is keyed by reference name,
not display name), plus the item's id and revision from the snapshot. Process-specific fields need
no special handling: they are ordinary controls naming ordinary reference names, and §3's three
states already cover the case where Twig does not carry one.

### 9. Twig appearance metadata — SEPARATE from the document

`WorkItemTypeAppearance (Name, Color, IconId)` is Twig's own look-and-feel opinion, not the
server's structure. It is **not folded into the detail document**; a host that wants it asks for it
alongside. Daniel agreed on the recommendation.

Reasons: a consumer reading fields should not have to receive styling opinions to do so; `IconId`
is meaningless to any host that is not a terminal with a matching glyph table; and keeping it out
preserves the ticket-0001 rule that `IconSet`'s glyph tables — whose own remarks constrain them to
BMP PUA because of a Spectre.Console width bug — never become terms of an external API. The
appearance record itself is already `public` and dependency-free, so this costs nothing.

### 10. What is explicitly NOT in the document

- No renderer types (`RenderNode`, `RenderTree`, `RenderAudience`, `Hint`, `Severity`).
- No `Terminal.Gui` or `Spectre.Console` anything.
- No `IFormLayoutProvider`, no async, no `CancellationToken` — acquisition stays behind
  Infrastructure.
- No `IPendingChangeStore`, no `IsDirty`, no `StagedIdentity`, no `PendingNotes`. Read-only
  construction never requires a persistence store; `WorkItem` is not the input (ticket 0001 §2).
- No glyphs.
- No relationship or hierarchy edges. Those are sibling projections, charted separately; this
  document is form-shaped.

### 11. Consequences for downstream tickets

- **0003 (prototype):** the external-host fixture must exercise all three field states, at least
  one non-`custom` page, and one contribution control — otherwise it proves only the happy path.
- **0004 (TUI migration):** `WorkItemFormView`'s ten hard-coded `TextField`s
  (`Views/WorkItemFormView.cs:28-41`) are replaced by walking this document. The three-state
  resolution is what makes that safe: today the view reads `item.Fields[key]` directly
  (`:285`) and inherits the core-field hole.
- **0005 (editing):** `ReadOnly` here is *reporting*, not an editing contract. 0005 defines what
  editing means; it must not reinterpret this flag as its authority.
- **New, non-blocking:** `FieldImportFilter`'s boolean exclusion is a projection-level data loss
  with a stated cause (string-only dictionary). It is not this ticket's to fix, and the third
  state makes it honest rather than silent. If it is ever fixed, the third state's population
  shrinks and no contract changes — which is the test that the three states are modelled at the
  right level.
