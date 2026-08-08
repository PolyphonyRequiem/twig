# Hostable work-item detail projection — Wayfinder map

## Destination

A build-ready specification for a framework-neutral work-item detail projection, backed by a verified external-host prototype. The specification preserves ADO's server-authored form structure and Twig-owned appearance vocabulary, gives read-only hosts no persistence dependency, defines optional editing capabilities explicitly, and provides a concrete migration route for Twig TUI.

The map ends at the implementation handoff. Shipping the package and production migrations belong to the build that follows.

## Lineage

- **ADO 155 — Expose a hostable work-item detail projection** is the work tracker and source acceptance brief.
- **Twig 1.0 Wayfinder, server-driven TUI editor decision** (`wayfinder-1.0/map.md`, ticket 1003) is settled input: the server's page → section → group → control structure governs the editor; widgets remain host-owned.
- **Twig 1.0 form-layout acquisition work** (ticket 1004 and `FormLayout`) is settled input: all four ADO levels and their order are preserved; column collapse is a rendering decision.
- This is a sibling map. It does not extend the completed Bench work or reopen Twig's surface architecture.

## Notes

- Domain vocabulary: `CONTEXT.md` is authoritative for names.
- Governing rule: **structure and projection are shared; rendering, frame, focus, sizing, scrolling, and application lifecycle belong to the host.**
- The first concrete customer is Bonsai's caller-owned duplicate-review pane. Twig TUI is the second customer and must consume the same projection.
- A test that injects its own projection or renderer does not prove hostability. The external-host prototype must construct the real public API from a consumer project and paint it inside a caller-owned frame.
- Preserve source values. Long/rich values may expose summaries, but the projection cannot truncate or discard the full value.
- Skills: `wayfinder`, `grilling`, `decision-mapping`, `substrate-consumer-ui-integration`, `capability-gate-review`, `integration-seam-verification`.
- One ticket per session.

## Decisions so far

- **Destination and architecture boundary** — confirmed by Daniel on 2026-08-07: this work should be wayfound before implementation; it is a projection/capability seam, not a shared renderer.
- **Public package boundary is `Twig.Domain`** (ticket 0001, pinned at `173d1673`): it is already packable as `PolyphonyRequiem.Twig.Domain` with zero project and zero runtime package references, multi-targets `net10.0;net11.0`, is AOT-clean, and already owns `WorkItemSnapshot`/`WorkItemTypeAppearance`/`FormLayout`. The only required source change is promoting `FormLayout`+`LayoutPage`/`Section`/`Group`/`Control` from `internal` to `public` via `PublicAPI.Unshipped.txt`; `IFormLayoutProvider`, `AdoIterationService`, `IPendingChangeStore`-backed editing, `IconSet` glyphs, and `Twig.RenderTree` stay off the consumer contract.
- **No node-link graph rendering — trees and lists only.** Cut by Daniel on 2026-08-07 after reading `docs/research/console-graph-rendering-prior-art.md` and `docs/research/console-graph-glyphs-and-interaction.md`. The surveys' own top recommendation was not a graph picture: it was a spanning tree with per-row edge-kind markers plus `(*)`-style located back-references (the `cargo tree` pattern), with an edge list grouped by relation kind as the narrow/machine fallback. Relationship *data* is still projected; only node-link layout is refused. Supporting evidence: no mainstream TUI draws a general node-link graph (k9s, lazygit, dua, btop are trees/lists; Bazel exports DOT instead), the crossing glyph vocabulary caps at two line weights with no bridge/diagonal/rounded-tee junctions, and a crossing cell has one foreground colour so colour-encoded edge kinds are undrawable exactly at crossings.
  **Two constraints survive the cut and still bind ordinary trees:** (1) ADO relations are cyclic in the wild — mutual `Related` links are trivially a 2-cycle — and `Spectre.Console.Tree` throws `CircularTreeException`, so any relation-following traversal needs its own visited set emitting reference markers *before* data reaches the renderer; (2) box-drawing `U+2500..254B`/`U+2550..2573` are `East_Asian_Width=Ambiguous` (double-width under CJK-configured terminals) while adjacent dashes/stubs are Neutral, and `Spectre.Console`'s `Cell.cs` caches width in an `sbyte[]` indexed by UTF-16 code unit — so *every* codepoint above `U+FFFF` mismeasures, which generalises the known Nerd Font SPUA bug and also rules out Symbols for Legacy Computing (`U+1FB00..1FBEF`).
- **The detail document carries every fact and lets the host drop what it wants** (ticket 0002, pinned at `74e2d6a1`): the server's four levels unchanged; each field control resolves to one of **three** states — has a value / empty on the server / not carried by Twig — because `FieldImportFilter` excludes all eight core fields (`System.Title` among them), every boolean, and unlisted read-only fields, so a naive lookup silently blanks a large slice of every form; non-`custom` pages and contribution slots are carried flagged rather than filtered; long values carry the full source value **and** a Twig-computed short form so hosts do not each cut differently; `ReadOnly`/`Visible` and `ControlType` are reported verbatim with no closed widget enum; and `WorkItemTypeAppearance` travels **separately** from the document rather than inside it.

## Not yet specified

- How a missing or unsupported server layout degrades without returning to a permanent hard-coded field list. **Untouched by ticket 0002, and narrowed by it:** 0002 defines the document when a layout exists, and the parse already distinguishes "no layout served" (null) from "an empty layout" (pinned by `AdoIterationServiceFormLayoutTests`). The open part is what a host receives in the null case — nothing, or a Twig-authored fallback arrangement over the fields it does carry. Note that 0002's third field state ("not carried by Twig") means a fallback cannot simply enumerate `WorkItemSnapshot.Fields` and call it a form.
- **Sibling projection primitives beyond the form.** Confirmed by Daniel on 2026-08-07: the detail form is not the only projection a host wants. Two more are known: the **hierarchy tree** (parent/child, carried today as `WorkItem.ParentId` — a strict tree) and **relationships** (`WorkItemLink` with `LinkTypes.Related` / `Predecessor` / `Successor` — cycles and multiple paths legal in the data). Twig's own render vocabulary knows only the first: `RenderNode.TreeView`/`RenderTreeBranch` model a row with children and cannot express a non-hierarchy edge. **Ticket 0001's package boundary already covers all of them** — `Twig.Domain` has zero project and zero runtime package references regardless of which primitive ships on it, and `WorkItemLink` is already `public` there. Node-link graph *rendering* is now cut (see Decisions), so the remaining open question is narrower: what the tree projection carries so a host can render relationships as marked rows and grouped lists rather than as a picture. Out of scope for this map — chart separately; do not widen ticket 0002, which is form-shaped.
- Package/API compatibility promises and versioning at the first external release. Sharpened by ticket 0001: the vehicle is `PolyphonyRequiem.Twig.Domain`'s existing `PublicAPI.Shipped.txt`/`Unshipped.txt` analyzer discipline, and the open part is now narrower — what compatibility promise attaches to the promoted layout types across `net10.0`/`net11.0`, and whether Domain's broad existing surface should be narrowed into a separate `Twig.Detail` package. Decide at 0006.
- Whether the external-host prototype belongs in this repo as a sample, a test project, or a sibling Bonsai spike. Sharpened by ticket 0001: whatever the location, it must consume `PolyphonyRequiem.Twig.Domain` as a package/project reference with no `Twig.Infrastructure`, `Terminal.Gui`, or `Spectre.Console`, and must construct a `FormLayout` from fixture data rather than through `IFormLayoutProvider`, whose only implementation is Infrastructure-internal and requires ADO authentication. Decide at 0003.

## Out of scope

- A shared Terminal.Gui, Spectre.Console, ratatui, or other renderer.
- Node-link graph layout in a terminal — lane gutters, layered box diagrams, adjacency matrices, arc diagrams. Cut 2026-08-07; see Decisions. Trees and lists only.
- Ownership of any host's frame, focus, keyboard routing, dimensions, scrolling, navigation, or lifecycle.
- Making Bonsai a second ADO client or exposing Twig authentication/infrastructure to consumers.
- Rebuilding the whole Twig TUI session model; this map only defines migration of item-detail semantics.
- Publishing or shipping the final package. That is the implementation handoff after this map closes.
