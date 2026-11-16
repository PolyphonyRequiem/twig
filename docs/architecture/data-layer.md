# Current data layer

The CLI initializes a fresh Core SQLite store, normally `.twig/core.db`.
`Twig.Store.Sqlite` persists provider-qualified snapshots, drafts, proposals and
operation journals. Current composition has no legacy cache/writer fallback or
local-data migration/adoption path.

The user registry and `benches.db` hold registered connections and user-wide
Bench intent. A worktree/session selects its own context. Workspace exclusions
are separate local view rules, not provider deletes or subtracting Bench rules.
The native provider's `native-authority.db` is a durable authority, not a
workspace cache; its source/owner identity cannot be replaced and silently
rebound.

Cached observations, staged drafts, verified published identities and journal
receipts are different kinds of evidence. A cache miss or unavailable source
must not be reported as deletion or an empty successful result. Native
creation recovery observes original authority receipts and verifies readback
without redispatching an uncertain creation.

Developer-local application data is disposable for development. That does not
authorize deleting credentials, source work, machine configuration or upstream
items, nor replaying an uncertain remote write after a reset.
