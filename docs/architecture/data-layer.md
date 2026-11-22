# Storage and data ownership

Twig's current data path uses `Twig.Core` and `Twig.Store.Sqlite`, not the former
`IWorkItemRepository`/`IPendingChangeStore` cache graph.

## Stores

| Data | Ownership |
| --- | --- |
| `.twig/core-workspace.json` | Worktree marker recording the admitted connection and actual Core store |
| `.twig/core.db` (default) | Workspace client mirror, typed drafts, plans, operation journal and permanent creation mappings |
| Native user registry and private credentials | Registered connection/identity metadata and separate private credential material |
| User-wide `benches.db` | Shared Bench definitions, rules, source dependencies and repository bindings |
| `native-databases/database-<name>.db` beside the user registry | One authoritative database for the Twig provider per explicit name, with independent stable source identity, items, revisions and creation evidence |
| Server private data directory | Server identity/instance guard, endpoint metadata, presentation sessions and native evidence |

An explicit absolute `TWIG_USER_HOME` selects the native metadata home.
Changing only the server's private data directory does not isolate user-wide
Benches or the native databases. Exact formats and safety checks are implementation
contracts, not files callers should synthesize or edit.

Native workspaces declare `nativeDatabase` in `twig.json` and pin that database's
UUID-backed connection in their Core store. Names are exact portable selectors,
not paths: 1-64 lowercase letters, digits or internal hyphens. Fresh initialization
creates a missing named authority or opens the selected existing one. Ordinary
reopen never creates or replaces it; missing databases, changed identities and
cross-database items/plans refuse. Repository roots and Benches are not
database identities. The old singleton authority is not adopted or migrated.

## Reads and drafts

`twig show <id>` reads a retained item; `--refresh` explicitly asks the provider.
`twig workspace --bench MyWork` reads that Bench, not a worktree selection.
Adding `--refresh` refreshes that Bench's scope without publishing drafts.
Cached observations are not proof of present access or current provider values.
Keep [local edits](../../GLOSSARY.md#local-edits) separate from observations.
A read or refresh must not silently publish local edits, erase local intent, or
turn missing observations into invented fields.

Core's `Draft` represents both local edits to an existing work item and seeds.
For local edits, `Baseline` holds the edit baseline and `Changes` holds the
intended field changes. A seed has no provider baseline.

For example, if Twig last observed an ADO Bug titled “Login fails” and the user
edits it offline to “Login fails after timeout,” the first title is the edit
baseline and the second is local intent. If ADO independently records “Sign-in
returns 500,” Twig won't know that title until it observes the change. Core's
merge compares all three titles and reports a conflict rather than choosing one.

Seeds have canonical UUIDv7 identities. Negative CLI IDs are convenience aliases,
not a second identity allocator or durable external keys. Verified publication
retains a permanent local-to-provider mapping.

## Writes and durability

Core canonicalizes the full plan and binds its digest to connection, schema,
store evidence and typed operations. Applying first persists an attempt before
provider dispatch. Acknowledgement and independent readback are distinct steps;
Applied is not the same as Verified.

SQLite transactions enforce Core commit invariants. They do not make a remote
request atomic with a local commit. A lost response can leave an Indeterminate
attempt, and an already-published item must never be recreated to repair local
delivery. GitHub transport sidecars correlate an original request/response but do
not replace the Core journal as execution authority.

See [plans](../features/plans.md) and the
[store contract](clean-core-contract.md#store-transactions-and-durability).

## Fresh initialization, not migration

`init` creates a new store and refuses existing or partial `.twig` state, databases,
and sidecars. It does not migrate, reset, repair, or adopt an old cache.
Developer-local application data is disposable; use a fresh explicitly scoped
workspace/home when needed. Never treat a reset as evidence about upstream writes,
or discard unknown-write evidence in order to retry.
