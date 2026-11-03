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

See [the contract and layer ownership](../../docs/architecture/clean-core-contract.md)
for canonical digest rules, schema/close-gate validation, per-effect receipts,
crash recovery constraints, store transactions and deferred phase boundaries.
