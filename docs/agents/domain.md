# Domain docs

## Layout and reading

This repository uses a single context: `GLOSSARY.md` at the repository root and
architecture decisions under `docs/adr/`.

Before exploring, read the glossary and ADRs relevant to the work. If a root
`GLOSSARY-MAP.md` is introduced later, follow its pointers to relevant context
glossaries as well.

If these files do not exist, proceed silently. Do not create placeholders or flag
their absence. Domain modeling creates them lazily as terminology and decisions
are resolved.

## Vocabulary and decisions

Use glossary terms consistently in tickets, proposals, code and tests. Reconsider
invented synonyms; note genuine terminology gaps for domain modeling.

Surface any conflict with an existing ADR explicitly rather than silently
overriding it.
