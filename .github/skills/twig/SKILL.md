---
name: twig
description: Use when discussing, operating, presenting, or reviewing Twig work items, or reporting their outcomes. Supporting Twig workflows use this foundation.
---

# Twig

## Minimum you should know

Twig connects your working environment to Azure DevOps (ADO) work tracking through local views and explicit read/change operations.

- **Connection:** the organization and project you are addressing. A Git remote alone does not identify the tracker connection.
- **Work item:** an identity with a type, status, fields and relationships. Discover the connected process's vocabulary and rules; do not assume one taxonomy.
- **Relationships:** parent/child means decomposition; predecessor/successor means dependency; Related means association. A relationship alone grants neither ownership nor permission to act.
- **Bench and active item:** a Bench is a named, saved view of work; the active item is a local selection. Neither is a work claim.
- **Local versus published:** reads may use cached information; seeds are local drafts. Local changes, publication and refreshed verification are distinct outcomes.
- **Change proposal:** intended tracker mutations. Review, authorization, application and verification are separate; presenting a change does not authorize it.

## Discuss, Present, Review

**Discuss** in contextual prose. Lead with type and ID. Link that identity when links are supported and the target URL is verified:

- Exact verified title: `[Type #123](url) — “Exact tracker title”`.
- Shortening or relevance: `[Type #123](url), the blocker for this work`.

Shorten follow-ups while unambiguous; restore type and ID when context shifts or another item could be meant. Include the connection when needed to distinguish identities. Missing identity details remain unknown.

Name the discussed item first, then the relationship from its perspective: “is a child of”, “depends on”, “blocks”, or “is related to”. Label inferred relationships.

**Present** a readable work-item hierarchy with identities and actual status wording from ADO. Keep dependencies and associations distinct from hierarchy; label partial or missing context.

**Review** adds proposed effects, preconditions, blockers and available choices. For change-proposal reviews, use the canonical `reviewModel` and [change procedure](references/changes.md); preserve every material operation and consequence.

## Adapt and report faithfully

Emphasize type and ID first, status/outcome second, description third. Say “status in ADO”; reserve “ADO process state” for process configuration. Categories may guide styling but do not replace actual status wording.

Use loaded presentation preferences and a compatible selected presenter within verified destination capabilities. Otherwise provide deliberate plain text. Color and icons supplement meaning. Follow the [presentation reference](references/presentation.md) when choosing or adapting a presenter; layout changes neither facts nor authority.

Disclose cached, stale, partial, unavailable or unverified knowledge where it could change interpretation; omit routine timestamp and provenance clutter.

Distinguish command success, applied changes, completed items and completed parent efforts. Name the scope actually completed and, when relevant, its nearest unfinished parent or consequential remaining work.

## Where to look next

| Need | Consult |
|---|---|
| Exact command syntax and behavior | `twig --help`, then targeted group or command `--help` |
| Read, navigate, author drafts or inspect process rules | [Operations](references/operations.md) |
| Author, apply, verify or recover tracker changes | [Changes](references/changes.md) |
| Choose presentation for this destination | [Presentation](references/presentation.md) |
| Conduct the work itself | The applicable work-item-driving workflow and process policy |

When only the executable is available, read the same bundled references with `twig --skill operations`, `twig --skill changes`, or `twig --skill presentation`.
