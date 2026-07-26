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
- Ticket 0011 (startup + observability) carries the only measured latency numbers in this map. Any ticket arguing about the cost of work should cite it rather than assert.

## Decisions so far
<!-- one line per closed ticket -->
- 0008: all six registration touch points (3 CLI, 3 MCP) are now guarded by build-time completeness tests rather than by hand — including constructor-level assertions, because .NET DI's greediest-satisfiable-constructor rule makes a bare resolution test pass on a degraded path. The guards found a live bug: `twig save` was dispatching to an unregistered `SaveCommand`.

- [What is twig for?](tickets/0001-what-is-twig-for.md) — A single-user local tool each dev runs independently; the shared substrate is ADO, never twig. The cache is disposable, the PENDING SET is the only thing twig owns, and it needs its own lifecycle: selective push per item, forced parent-before-child sequencing, push-and-recover with durable intent recorded BEFORE the ADO call, interactive conflict resolution for humans and warn-and-advise for agents/scripts.

## Not yet specified
- Whether the TUI ships as its own product or as a mode of one binary. Partly clarified (owner, 2026-07-26): the TUI is a CLI *concept* — same user, same terminal, same mental model — but "can be its own product." That places it conceptually without deciding packaging, so whether `src/Twig.Tui` keeps its own composition root and output stack stays open. See tickets 0002 and 0007.
- Migration sequencing: if both the persistence model and the surface seam change, which lands first and how the intermediate state stays shippable.
- Whether `docs/specs/` should remain hand-written prose or be generated from code once the surfaces share a seam.
- What twig's public contract actually is for external consumers, and what a breaking change means for it.

## Out of scope
- Doc-rot remediation: ~110 mechanical documentation corrections found in the audit, each with a path:line citation. Real work, but a grind rather than a design conversation — belongs in its own issue alongside #276, not in this map.
- The historical `twig-structural-audit.doc.md` and `twig-architecture-analysis.doc.md` (both 2026-03-16, repo genesis). Ruled archaeology by the owner; superseded by the current ledgers.
- Fixing issue #280 (seed ID recycling). Filed separately as a bug. In scope for this map only as an input to ticket 0003, which decides the identity model.
