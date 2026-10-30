# Twig.Core

Additive clean-core foundation for Epic #1220. A standalone, AOT-compatible
`net10.0;net11.0` library with immutable provider-neutral models, pure invariants
and two ports (`IProvider`, `IStore`). No production commands use it yet.

Phase 2 adds [Twig.Store.Sqlite](../Twig.Store.Sqlite/README.md), an isolated
SQLite/WAL implementation of the accepted snapshot/CAS boundary. It does not
replace legacy workspace storage or wire commands to the new core.

See [the contract and layer ownership](../../docs/architecture/clean-core-contract.md)
for canonical digest rules, schema/close-gate validation, per-effect receipts,
crash recovery constraints, store transactions and deferred phase boundaries.
