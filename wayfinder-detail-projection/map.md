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

## Not yet specified

- Which rich/control kinds require typed document values versus a generic raw-value escape hatch.
- How a missing or unsupported server layout degrades without returning to a permanent hard-coded field list.
- Which Twig-owned appearance metadata belongs in the core document versus an optional appearance companion.
- Package/API compatibility promises and versioning at the first external release; sharpen after the package-boundary research.
- Whether the external-host prototype belongs in this repo as a sample, a test project, or a sibling Bonsai spike; sharpen after the public boundary is known.

## Out of scope

- A shared Terminal.Gui, Spectre.Console, ratatui, or other renderer.
- Ownership of any host's frame, focus, keyboard routing, dimensions, scrolling, navigation, or lifecycle.
- Making Bonsai a second ADO client or exposing Twig authentication/infrastructure to consumers.
- Rebuilding the whole Twig TUI session model; this map only defines migration of item-detail semantics.
- Publishing or shipping the final package. That is the implementation handoff after this map closes.
