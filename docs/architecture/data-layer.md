# Storage and data ownership

Twig's current data path uses `Twig.Core` and `Twig.Store.Sqlite`, not the former
`IWorkItemRepository`/`IPendingChangeStore` cache graph.

## Stores

| Data | Ownership |
| --- | --- |
| `.twig/core-workspace.json` | Worktree marker recording the admitted connection and actual Core store |
| `.twig/core.db` (default) | Workspace client mirror, typed drafts, proposals, operation journal and permanent creation mappings |
| Native user registry and private credentials | Registered connection/identity metadata and separate private credential material |
| User-wide `benches.db` | Shared Bench definitions, rules, source dependencies and repository bindings |
| Worktree `core-context.json` | Local active-item history and Bench selection |
| `native-authority.db` beside the user registry | Authoritative Twig-native items, stable source identity, revision and creation evidence |
| Daemon private data directory | Daemon identity/instance guard, endpoint metadata, presentation sessions and native evidence |

An explicit absolute `TWIG_USER_HOME` selects the native metadata home.
Changing only the daemon's private data directory does not isolate user-wide
Benches or the native authority. Exact formats and safety checks are implementation
contracts, not files callers should synthesize or edit.

## Reads and drafts

`show` reads retained items by default; `--refresh` explicitly asks the provider.
Cached observations are not proof of present access or current remote values.
Typed local drafts remain separate from observations. A read or refresh must not
silently publish drafts, erase local intent, or turn missing observations into
invented fields.

Seeds have canonical UUIDv7 identities. Negative CLI IDs are convenience aliases,
not a second identity allocator or durable external keys. Verified publication
retains a permanent local-to-provider mapping.

## Writes and durability

Core canonicalizes the full proposal and binds its digest to connection, schema,
store evidence and typed operations. Applying first persists an attempt before
provider dispatch. Acknowledgement and independent readback are distinct steps;
Applied is not the same as Verified.

SQLite transactions enforce Core commit invariants. They do not make a remote
request atomic with a local commit. A lost response can leave an Indeterminate
attempt, and an already-published item must never be recreated to repair local
delivery. GitHub transport sidecars correlate an original request/response but do
not replace the Core journal as execution authority.

See [proposals](../features/proposals.md) and the
[store contract](clean-core-contract.md#store-transactions-and-durability).

## Fresh initialization, not migration

`init` creates a new store and refuses existing or partial `.twig` state, databases,
and sidecars. It does not migrate, reset, repair, or adopt an old cache.
Developer-local application data is disposable; use a fresh explicitly scoped
workspace/home when needed. Never treat a reset as evidence about upstream writes,
or discard unknown-write evidence in order to retry.
