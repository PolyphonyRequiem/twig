# Twig.Core

A standalone, AOT-compatible `net10.0;net11.0` library with immutable
provider-neutral models, pure invariants and two ports (`IProvider`, `IStore`).
Native CLI and panel consumers use the same item, query and Bench model.

Bench selectors are an additive union of individual pins, subtrees and query
rules. Workspace-view exclusions remain separate from Bench ownership. Native
user-wide definitions outlive their originating worktree; context selections
and local draft evidence remain worktree-local.

Planning rules preserve intent rather than result IDs. `PathEqual` is
case-insensitive exact path matching; `Under` additionally matches descendants
at a path-component boundary. Iteration rules retain absolute paths or
canonical `@Current`, `@Current+N`, and `@Current-N` expressions. Relative
evaluation requires a caller-supplied time and the pinned native team's cached
calendar. Every dated, overlapping current iteration participates in offset
resolution; missing current iterations and out-of-range offsets do not broaden
the rule. Missing or different-team calendar evidence is unavailable, not an
empty success. Calendar metadata is outside the mutation/schema digest and is
refreshed explicitly through the existing provider port.
Removing the final configured iteration retains an explicitly empty iteration
union inside that planning rule. Remaining area/ownership configuration is not
silently promoted into a broader query. Empty resolved scopes acquire no work
items; independent Bench pins and protected draft debt remain separate.

Saved-query and backlog pins retain the native source ID and canonical team in
the query model. Explicit acquisition discovers the actual query hierarchy or
backlog roster, obtains native membership, reads item bodies in documented
200-item batches, and mirrors only complete observations. Query/backlog link
rows are not substituted for native item relationship evidence.

Derived membership evidence is cached separately from Bench definitions, scoped
to the connection and selected/discovered team. Cached evaluation never executes
a query or refreshes a backlog. A known complete empty membership is empty; a
missing/incomplete/different-team membership, or a member missing from this
context's item mirror, is unavailable. Neither outcome rewrites a saved rule into
today's result list. Acquisition and cache/output budgets fail explicitly rather
than returning truncated membership.

See [the contract and layer ownership](../../docs/architecture/clean-core-contract.md)
for canonical digest rules, schema/close-gate validation, per-effect receipts,
crash recovery constraints, store transactions and deferred phase boundaries.
