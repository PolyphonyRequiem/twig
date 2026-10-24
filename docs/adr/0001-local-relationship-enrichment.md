---
status: proposed
---

# Local relationship enrichment is declared per provider, native-first, and never silent

Twig offers one relationship model (hierarchy, predecessor/successor, related) across providers. Where a provider natively supports a relationship, Twig uses and labels the native operation. Only an evidenced semantic gap may activate enrichment, each provider adapter declares where enrichment data lives, and no emulation is active today. Epic #1170, Feature #1190. Acceptance of this ADR is a human decision and is not implied by its presence.

## Decisions

1. **Storage engine.** SQLite stays until it is shown not to scale. Local-owned relationships live in the durable store (`pending.local_relationships`, v14 `storage` and `publication` columns), never in the rebuildable mirror (`work_item_links`). Refresh replaces only provider observations. Daniel, with #1168's durable-file plus rebuildable-mirror split; no crash-safety claim spans the two files.
2. **Storage ownership is per provider, not global.** Each adapter declares, per emulated capability, `LocalOnly` or `LocalWithProviderProjection` as static versioned descriptor data. Native capabilities declare no emulation storage. Unsupported capabilities are refused explicitly.
3. **Implementation kind and runtime availability are separate.** `Native | Emulated | Unsupported` is descriptor data. `Available | Unavailable | Unknown` is per-connection evidence. Denied, throttled, failed or unprobed discovery is Unknown or Unavailable, never absence, and never changes the declared kind.
4. **GitHub and ADO both support hierarchy and blocking dependencies natively.** Neither declares emulation for them. The GitHub acceptance criterion is met by native operations, labelled native.
5. **Distinct refusals.** `relationship-unsupported` and `relationship-emulation-unavailable` stay separate. A native write failure is never retried as an emulated write.
6. **Provenance.** `show`, `link` and `connection capabilities` derive relationship origin only from the resolved descriptor and valid probe evidence, never from a provider-name heuristic. An unresolvable provider fails closed (`provider-unavailable`, `provider-unknown`, `provider-unconfigured`).

## Sync, conflict and recovery (apply to `LocalWithProviderProjection`; proposed, not implemented)

- Writes go through the proposal authorization and journal. A changed metadata destination or material effect requires re-review.
- Use provider conditional mutation where it exists. Otherwise refuse when concurrent user content cannot be preserved; never call read-then-write compare-and-swap.
- Preserve non-Twig content. Remote metadata is untrusted: validate ownership, schema version, canonical endpoints, cardinality and graph constraints. Unknown or malformed owned metadata fails explicitly and deletes nothing.
- An ambiguous remote outcome stays pending reconciliation. Inspect receipt evidence before retry; never append duplicate edges.
- Removal is a durable tombstone until verified against the shared projection, so a stale observation cannot resurrect it.
- Native-versus-local parentage conflicts and concurrent enrichment changes surface as conflicts. No last-writer-wins and no automatic conversion to native.
- Local commit, pending publication, remote receipt and verified readback are reported separately; there is no distributed transaction.

## Activation preconditions

Emulation stays off. Wiring any emulated route needs a separate authorization, a red-first proof on real storage composition (survival across refresh and rebuild, readback, content preservation, concurrent change, ambiguous retry, tombstones, native-failure refusal) and a declared policy treatment: `DeclaredPolicyEvaluator`'s parent-type and dependency-integrity checks must either read Twig-local overlay edges or return `unavailable` for affected items (#1169 merge obligation 2, `docs/specs/cloudvault-policy-decisions.spec.md`). Silently omitting local edges from policy, or downgrading a native declaration to make emulation apply, is not allowed.

## Considered options

- Global DB-only or hybrid policy: rejected by Daniel in favour of per-provider declaration.
- Provider metadata only: lacks independent local durability; not offered.
- Sync server, event sourcing or a second journal: out of scope.
- Replacing SQLite: deferred.

## Consequences

- Body-marker or label projection formats are not approved by this ADR; each needs its own adapter-level decision.
- `show` and `link` labelling for GitHub depends on provider read routing (#1167, #1177) and is delivered by #1191.

## Open decisions for acceptance

1. Should capability discovery be static descriptor data only, or connection-scoped async evidence on top of it? This draft assumes both, with descriptor as the declared kind.
2. What canonical relationship vocabulary and provenance fields are required beyond origin and publication state?
3. How should partial or permission-denied discovery be shown per capability, beyond Unknown versus Unavailable?
4. Does the proposal journal bind projection destinations as a new effect kind, or reuse existing effects?
