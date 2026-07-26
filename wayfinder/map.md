# Twig architecture — wayfinder map

## Destination
A twig codebase whose architecture matches its stated ideology — four peer surfaces (human CLI, AI/MCP, toolchain JSON, TUI) over deep shared modules — with a persistence model chosen on evidence, and documentation that is true. Reached when every ticket below is closed and nothing structural is left to decide.

## Notes
- Domain vocabulary: `CONTEXT.md` at repo root is authoritative for NAMES. Architecture vocabulary: the `codebase-design` skill (module, interface, depth, seam, adapter, leverage, locality; the deletion test; one adapter = hypothetical seam, two = real).
- Skills every session should consult: `codebase-design`, `grilling`, `decision-mapping`, `improve-codebase-architecture`.
- Evidence ledgers live in `%TEMP%\twig-review\`: ledger-specs.md, ledger-architecture.md (arch-a/arch-b), ledger-parity.md, candidates-surface.md, candidates-state.md, candidates-output.md. ~130 audited findings, all cited path:line.
- Twig has FOUR surfaces and THREE composition roots (CLI, MCP, TUI). Only the CLI references Twig.RenderTree.
- This is a PLANNING map. Produce decisions, not deliverables. One ticket per session.
- Doc-rot (~110 mechanical corrections) is deliberately NOT in this map — see Out of scope.

## Decisions so far
<!-- one line per closed ticket -->

## Not yet specified
- Whether the TUI is committed or exploratory in the long run — it has 774 lines of source, 1,064 lines of tests, 8 commits, last touched 2026-07-11, and the owner reports it is "coming back to the surface." Its weight changes how much the rendering seam matters.
- Migration sequencing: if both the persistence model and the surface seam change, which lands first and how the intermediate state stays shippable.
- Whether `docs/specs/` should remain hand-written prose or be generated from code once the surfaces share a seam.
- What twig's public contract actually is for external consumers, and what a breaking change means for it.

## Out of scope
- Doc-rot remediation: ~110 mechanical documentation corrections found in the audit, each with a path:line citation. Real work, but a grind rather than a design conversation — belongs in its own issue alongside #276, not in this map.
- The historical `twig-structural-audit.doc.md` and `twig-architecture-analysis.doc.md` (both 2026-03-16, repo genesis). Ruled archaeology by the owner; superseded by the current ledgers.
- Fixing issue #280 (seed ID recycling). Filed separately as a bug. In scope for this map only as an input to ticket 0003, which decides the identity model.
