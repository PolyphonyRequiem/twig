---
description: 'Current living documentation standards and archive boundaries.'
applyTo: '**/*.doc.md'
---

# Documentation

Use the nearest current document's structure; there is no Octane configuration
or required external template. Match the user's requested scope and keep one
authoritative current description rather than duplicating command catalogs.

- Ground claims in current source and executable-matched help.
- Keep current CLI, Windows daemon and public RenderTree SDK distinct.
  Infrastructure is internal/nonpackable, not an external SDK.
- Update canonical command Markdown/index when behavior changes; declaration-
  derived help owns syntax/defaults and bundled Markdown owns behavior/examples.
- Remove unsupported installation recommendations and retired command examples.
  Do not revive integrations to satisfy old prose.
- Preserve ADRs, Wayfinder, plans, research and evidence as clearly archived
  material. Archived intent is not proof of current delivery or qualification.
- State unavailable evidence, partial results and product boundaries explicitly.
- Keep secrets, private identifiers and work-item contents out of telemetry and
  public documentation.

Use GitHub-flavored Markdown. Prefer concise prose and tables. Use diagrams only
when they add understanding; keep container and component abstraction levels
separate. Pseudocode illustrates intent; exact current CLI examples are
appropriate for operator guidance. Avoid fragile source line-number claims and
unverified performance/coverage totals.
