# Twig domain language

Current product vocabulary for the CLI, native daemon and RenderTree SDK.
Source and executable-matched help decide implementation details; historical
ADRs, Wayfinder rulings and research do not expand today's supported catalog.

| Term | Meaning |
|---|---|
| Core | Provider-neutral work-item semantics, proposals, policy, journals and ports in `src/Twig.Core` |
| Connection | A registered provider/source/principal binding; not inferred from a Git remote or ambient account |
| External identity | Provider kind, stable resource scope and opaque native ID; never just an unqualified integer |
| Local identity | A durable Twig-local UUID; not a published provider ID or temporary display alias |
| Observation | Cached provider facts and their revision evidence; incomplete or unavailable is not empty |
| Bench | A named user-wide saved view holding selectors/pins, not a claim, sync unit or publication destination |
| Context | A local selection/view in a worktree or daemon session; selecting it does not grant remote authority |
| Seed | An unpublished local draft; it becomes published only after authoritative creation and verified mapping |
| Pending change | Local staged intent, not a successful provider mutation |
| Proposal | A current version-3 declarative change document, identified by canonical SHA-256 digest |
| Preview | Validation and observation of intended effects, possibly local journal import; never publication |
| Apply | Digest-confirmed execution through Core, subject to current provider, policy, identity and journal checks |
| Journal | Durable operation/attempt outcomes and recovery authority; independent of presentation and transport logs |
| Indeterminate | A possibly successful write whose outcome is not established; never permission to replay |
| Native authority | User-owned durable `native-authority.db`, separate from each workspace mirror and journal |
| Daemon | Current Windows native host (`Bench.Server.exe`); owns authenticated localhost service lifetime |
| RenderTree | Public presentation SDK; rendering does not grant mutation or approval authority |
| Infrastructure | Internal native application services/composition, not a supported external SDK |

## Rules

- One concept has one name. Qualify “workspace” when it means an invocation
  directory, Core store, connection projection or saved view.
- Discover provider types, states, fields, relation kinds and close gates from
  the bound schema. No process-template names or state names belong in generic
  product logic.
- Hierarchy means decomposition; dependencies mean ordering; Related means
  association. None imply ownership or permission.
- Say “status in ADO” for an item's state; reserve “ADO process state” for schema
  configuration. Preserve actual discovered status wording.
- Cached facts, declared capabilities, recorded selection, verified remote
  outcomes and completed work are different claims.
- A Bench stores intent/rules, not authoritative result snapshots. It is
  user-wide; repository/worktree selections remain contextual.
- Mutation authority belongs to native admission, digest confirmation, provider
  checks and the Core journal, never a renderer, receipt alone or transport log.

## Historical vocabulary

Legacy `Twig.Domain` aggregates, negative seed identities, `PlanDefinition`,
`IPlanLifecycleService`, standalone TUI/MCP hosts and presentation envelopes
belong to pre-1.0 design history. They are not current SDK contracts or alternate
product routes. See [the documentation index](docs/README.md) for preserved
historical material and [the current composition](docs/architecture/overview.md)
for supported boundaries.
