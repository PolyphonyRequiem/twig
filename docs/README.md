# Twig documentation

## Current product

| Reference | Scope |
|---|---|
| [Root README](../README.md) | Supported delivery, capability matrix, installation and validation |
| [Architecture](architecture/overview.md) | CLI, Core/provider/store composition, native daemon and public RenderTree boundary |
| [Build and release](architecture/build-and-release.md) | SDK, current artifacts, signing and routine versus release-candidate gates |
| [Command reference](commands/README.md) | Bundled current command behavior; executable help supplies exact declarations |
| [Proposals](features/proposals.md) | Current v3 document, digest, journal and verified outcomes |
| [Skills](features/skills.md) | Explicit generic guidance installation and ownership |
| [Agent tracker guidance](agents/issue-tracker.md) | Tracker scope and mutation discipline |

The CLI, native Windows daemon and RenderTree SDK are current. Infrastructure is
internal, not a public package. The daemon's read-only MCP interface is current;
the old standalone MCP host, TUI/Herdr, OMP and item-companion adapters are retired.
There is no compatibility launch, migration or automatic companion route.

## Historical material

`adr/`, `projects/`, `research/`, `incidents/`, `specs/` and the repository's
`wayfinder/` and `wayfinder-1.0/` retain decisions, investigations and evidence.
They may describe removed commands, models and executables. Treat those details
as **archived context**, not installation instructions, supported APIs or current
acceptance claims. Historical test captures do not certify the current build.

Use the system SDK in `global.json`; follow the root README and `AGENTS.md` for
bounded serial validation. Do not run broad suites merely because old docs name
them, and do not restore obsolete tools to execute an old evidence recipe.
