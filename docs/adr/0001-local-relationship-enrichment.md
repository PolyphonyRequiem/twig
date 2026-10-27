---
status: accepted
tracked_in: [1190]
---

# Local relationship enrichment is declared per provider, native-first, and never silent

Accepted for the 1.0 scope: use provider-native relationship operations, and allow an explicit durable Twig-local Related association only when the selected provider declares Related unsupported. This does not authorize other local-only relationship kinds or provider-metadata projection. Provider-projected emulation remains deferred and inactive.

## Decisions

1. **Storage engine and ownership.** SQLite stays until it is shown not to scale. Local-owned relationships live in the durable store, never in the rebuildable mirror (`work_item_links`). #1168's durable-file split keeps the durable `local_relationships` data separate from provider observations; no crash-safety claim spans the two files. Refresh replaces only provider observations.
2. **The accepted 1.0 enrichment is narrowly local-only.** Native provider operations remain the route for declared-native relationships. A provider-native write failure is not a reason to fall back. A provider-declared unsupported kind is refused for provider mutation; the separate explicit local-only Related creation is allowed only when the selected provider declares Related unsupported and both endpoints are existing live local records. List and removal operate on existing Twig-local edges without requiring live endpoints or provider resolution. These edges remain distinct from provider observations and are never published to or translated by a provider. This exception does not apply to other kinds.
3. **Implementation kind and runtime availability are separate.** `Native | Emulated | Unsupported` is static descriptor data. `Available | Unavailable | Unknown` is per-connection evidence. Denied, throttled, failed or unprobed reads never downgrade the declared kind. In particular, GitHub dependencies may remain `native` while their availability is `unavailable`; absent evidence is `unknown`, not evidence of no relationships.
4. **The report contract stays compact and explicit.** Relationship kind, declared implementation, storage/publication where relevant, and per-kind availability with its reason are distinct facts. A provider-read relationship may be labelled `provider-native` only when it is a valid observed relationship matching the provider and resource scope and the resolved provider descriptor declares that kind native. A local-only association is labelled `twig-local` from its explicit durable-store origin, not from a provider descriptor. Unknown or denied read evidence does not mean the relationship set is empty. Capability reporting is read-only and starts no background probe; availability evidence does not authorize mutation.
5. **GitHub and ADO support hierarchy and blocking dependencies natively.** Their declarations remain native; local enrichment must not replace these native operations. GitHub relationship proposals explicitly select `observed-non-atomic` concurrency, check the observed item token immediately before writing, and verify both endpoints through native readback. This is a reviewed check-then-write, not server CAS: another actor can change either endpoint between requests. Exact-revision operations remain refused.
6. **Distinct refusals and provenance.** `relationship-unsupported` remains distinct from `relationship-emulation-unavailable`. A provider-native origin requires a valid provider-read observation matching the provider and resource scope plus a resolved descriptor declaring that kind native; Twig-local origin comes from the explicit durable-store record. Per-connection evidence supplies availability and its reason, never a provider-name heuristic. An unresolvable provider fails closed (`provider-unavailable`, `provider-unknown`, `provider-unconfigured`).

## Deferred: provider-metadata projection (inactive; not accepted implementation)

Provider-metadata projection, including labels or descriptions, is deferred from 1.0. `LocalWithProviderProjection` remains inactive. No projection format, remote metadata route, projection effect/schema, or associated conflict/recovery behavior is approved or claimed shipped here. Any future projection requires a separately scoped design and activation decision, including its format, concurrency, preservation, retry, tombstone, and readback behavior.

## Activation preconditions

The accepted explicit local-only Related association does not claim provider support and is not provider-projected emulation. Any future provider-projected emulated route remains inactive and requires a separately scoped design and activation decision, a red-first proof on real storage composition, and a declared policy treatment: `DeclaredPolicyEvaluator`'s parent-type and dependency-integrity checks must either read Twig-local overlay edges or return `unavailable` for affected items (#1169 merge obligation 2, `docs/specs/declared-policy-decisions.spec.md`). Silently omitting local edges from policy, or downgrading a native declaration to make emulation apply, is not allowed.

## Considered options

- A single global emulation-storage policy: rejected by Daniel in favour of per-provider declaration.
- Provider metadata only: lacks independent local durability; not offered.
- Sync server, event sourcing or a second journal: out of scope.
- Replacing SQLite: deferred.

## Consequences

- Body-marker or label projection formats are not approved by this ADR; each needs its own adapter-level decision.
- The 1.0 contract deliberately keeps native provider relationships and explicit local-only enrichment separate. Native GitHub operations use four fixed hierarchy/dependency mutation shapes through the existing proposal authorization and journal, not a local fallback or arbitrary GraphQL.
- Preview freezes the immutable provider target, attachment/binding revisions, selected credential account, and authenticated viewer ID. Apply rechecks that origin using the credential captured for the write. Native reads verify that both endpoints belong to the bound repository. Login and repository slugs are locators, not authority.
- GitHub snapshot tokens are `github-observed:` SHA-256 fingerprints of complete observed fields and canonical relationships. They are not native revision numbers, do not detect changes reverted between reads, and do not claim a transactionally consistent GraphQL snapshot. A changed token refuses before send; an uncertain response is read back without replay.
- For GitHub version-2 proposals the existing workspace `organization`/`project` fields hold the configured host and `owner/repository` locator. The immutable repository/owner IDs in captured origin, not those strings, bind authority.

## Deferred decisions outside this 1.0 scope

Provider-metadata projection and its format, effect/journal representation, and conflict/recovery behavior are deferred. The accepted report model uses the existing canonical relationship, implementation, origin, storage/publication, availability, and per-kind reason vocabulary; this ADR does not add further fields or a new schema.
