# Twig.Store.Sqlite

Phase 2 of Epic #1220: the additive SQLite implementation of `Twig.Core.IStore`.
It references only Core and the centrally versioned SQLite packages. Both shared
library targets (`net10.0;net11.0`) enable AOT compatibility analyzers and disable
reflection-based JSON. Production commands and legacy storage are unchanged.

## Database lifecycle

Call `SqliteStore.CreateAsync(absoluteFilePath)` **only for a new, explicitly chosen
database file in an existing directory**. It reserves that file with `CreateNew`;
an existing file is never adopted, overwritten, truncated or migrated. There is
no default `.twig` path, directory discovery, workspace reset or migration.
Do not pass a legacy `twig.db`. A separate name such as `clean-core.db` in an
isolated test/application directory is appropriate; no CLI creates it yet.

`OpenAsync(absoluteFilePath)` requires an existing clean-core database. It checks
application ID `0x54574353`, schema version `1`, the snapshot table, and WAL mode,
without changing the existing database's format or journal mode. These checks
also run inside every read/write transaction. Missing files, foreign/legacy
databases, unsupported versions and incompatible journal modes return an explicit
`StorageUnavailable` result. There is no automatic repair. Failed initialization
retains its new file for inspection; deliberately choose a different new path or
inspect/remove that exact failed file yourself. Cancellation propagates rather
than becoming success or an empty snapshot.

## Transaction and serialization boundary

One `core_snapshots` row stores one coherent snapshot per **exact Connection**:
provider, resource scope, immutable destination and identity reference. The
connection key is source-generated JSON, not delimiter concatenation or a lossy
hash. Different principals/destinations do not share attempts or mirrors. An
absent partition reads as version 0; its first successful commit persists the
binding and version 1. Connection credentials, schema discovery and active-item
context are not part of this adapter.

`ReadAsync` uses a read transaction. `CommitAsync` takes an SQLite IMMEDIATE writer
transaction, reads the current version, checks the caller's CAS, enforces
`Core.ValidateCommit`, checks proposal connection bindings and validates the
serialized roundtrip, then replaces that one row and commits. Any refusal,
cancellation or write error rolls the transaction back. A competing writer either
observes the newer version or reports a busy conflict (five-second lock wait);
the store never retries effects. Connections are per operation, pooling is off,
WAL is required, and writes use `synchronous=FULL`.

The versioned typed persistence DTOs use this adapter's source-generated
`TwigJsonContext`, never runtime polymorphism/reflection. Decoding rejects unknown,
duplicate, missing required or invalid members/versions, mismatched proposal
digests and inconsistent journal evidence. Missing fields, explicit null, empty
text, raw payloads, offset-bearing instants, ordered lists, opaque revisions and
provider-scoped identities remain distinct. Proposals are reconstructed and their
canonical digest checked; journal rows are reconstructed through the core's legal
Begin/Record transitions. No stored target is rewritten on publication.

The snapshot boundary intentionally rewrites a whole connection document. This
favors a small, auditable correctness implementation over SQL query/index/row-delta
optimization; large mirrors are a known scaling limitation. Callers must supply
the complete next snapshot, preserving local drafts/Bench definitions during
mirror refresh. Explicit draft/Bench edits are allowed by the accepted contract.
Core rejects losing local edges/tombstones, retained intent, history or mappings.

Import all Planned operations together; commit Applying and its durable attempt
before transport; commit acknowledgement, journal advance, creation mapping and
available mirror together; then commit verification and acquired observations.
The adapter has **no network calls** and provides local atomicity only. WAL is
not distributed atomicity. A failed post-dispatch acknowledgement commit leaves
the durable Applying fence, not permission to replay. Attempts, including unknown
creation outcomes, fence replacement proposal digests. Native settlement and
provider/orchestrator recovery remain later work.

## Focused verification

The supported solution builds this project and `Twig.Store.Sqlite.Tests`. Both
Linux and Windows CI run the real-SQLite boundary cases through the TRX reconciler:

```powershell
python tools\run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools\run-bounded.py --timeout 60 -- python tools\verify-trx.py -- dotnet test tests\Twig.Store.Sqlite.Tests\Twig.Store.Sqlite.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~Twig.Store.Sqlite.Tests" --logger "trx;LogFileName=store.trx" --results-directory artifacts\test-logs\core-store
```

Tests use unique disposable temporary files, not user workspaces. They exercise
independent-handle CAS acquisition, WAL reader isolation, injected write rollback,
reopen durability, full value/draft/Bench/proposal roundtrips, receipts and
mapping atomicity, mirror/local tombstone separation, and no-blind-replay fences.
The tightly coupled Core fix compares receipt/error evidence structurally: the
same durable history cannot depend on `ImmutableArray` backing-array identity.
Its regression fails on the detached phase-1 production Core and rejects actual
evidence rewriting after the fix.
