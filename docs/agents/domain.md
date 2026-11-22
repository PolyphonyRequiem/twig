# Domain docs

## Layout and reading

This repository uses one documented context, Twig work management. The
[root glossary](../../GLOSSARY.md) owns the shared language; the
[architecture overview](../architecture/overview.md) explains how it maps to
the implementation. Read architecture decisions under `docs/adr/` when present.

Provider adapters, workspaces, and presentation surfaces are boundaries, not
separate glossaries. In particular, Bench organization reuses work item
identity without owning it, and Plan execution records outcomes without
redefining the intended changes. Revisit the split if a part of Twig needs
the same terms to mean different things or develops its own domain rules
that are clearer when documented separately.

Before exploring, read the glossary and ADRs relevant to the work. If a root
`GLOSSARY-MAP.md` is introduced later, follow its pointers to relevant context
glossaries as well.

If these files do not exist, proceed silently. Do not create placeholders or flag
their absence. Domain modeling creates them lazily as terminology and decisions
are resolved.

## Vocabulary and decisions

Use glossary terms consistently in tickets, plans, code and tests. Reconsider
invented synonyms; note genuine terminology gaps for domain modeling.

Surface any conflict with an existing ADR explicitly rather than silently
overriding it.
