---
id: 0005
title: Persistence model
type: research
status: closed
blocked_by: [0001, 0004]
---

## Question

Is a document model better than the current relational one for twig's data, and if so should the documents be work-item files on disk with an indexer? Full evidence in `%TEMP%\twig-review\evidence-persistence.md`.

**The store is barely relational already.** 14 tables declared / 10 live / 2 dead. **Zero JOINs and zero recursive CTEs in the entire codebase.** Exactly **one** FOREIGN KEY (`SqliteCacheStore.cs:174`) — 6 further logical references are undeclared, and there are 0 cascades, 0 CHECKs, 0 triggers, 0 views. There are **no migrations at all**: the schema is a single C# const string, and a version mismatch calls `DropAllTables()` (`SqliteCacheStore.cs:15,86-92,143-261`, now at v10). 11 indexes, none reaching into `fields_json`. Access pattern by call site is **~127 document-shaped vs ~57 relational (≈69/31)**.

**What relational genuinely earns:** a `NOT EXISTS` orphan anti-join (`SqliteWorkItemRepository.cs:212-218`), a phantom-dirty cross-table anti-join as one atomic UPDATE (`:371-376`), and navigation cursor ordering over `AUTOINCREMENT` with ring trim (`SqliteNavigationHistoryStore.cs:61,80,104`).

**What it costs:** the single FK is the documented root cause of #268/#269/#270/#271 — including the chain where a constraint violation surfaced as "cache corrupt, run `init --force`", advising the user to destroy unpushed work (`Program.cs:324-328`, `SeedPublishOrchestrator.cs:249-261`, `SeedDiscardOrchestrator.cs:125-129`). The FK ordering rule is enforced by **comment** (`IPendingChangeStore.cs:30-33`).

**Files-on-disk ledger: 7 clear breaks** (WAL concurrency, the 5-table publish transaction, set-based UPDATE/DELETE, `NOT EXISTS` scans, `AUTOINCREMENT` ordering, single-file corruption identity), 2 partial, 3 non-issues — 2 of which are net simplifications. `ExportedWorkItem`/`WorkItemExportFormat` prove id + rev + full field bag round-trips to tested markdown, but the format is **lossy by design**: 12 excluded system fields, and no parent_id / dirty / seed / sync state.

Files-on-disk forces the source-of-truth question: is the file a disposable cache, or a durable local log that syncs to ADO? Those are different products, and 0001 decides which.

## Answer

**The question's axis is wrong, and answering it as asked would not discharge the ticket.**
Document-vs-relational is already settled by the code; the live axis is **durability**. Twig
splits its store in two by durability class, and stays on SQLite for both.

### 1. Document vs relational is already decided -- by the code, not by us

Twig already uses SQLite as a keyed document store. The evidence file settles this without
needing a decision:

- Access pattern is **~127 document-shaped vs ~57 relational (~69/31)** by call site. The two
  most-used operations in the product are `GetByIdAsync` (52 sites) and `SaveAsync` (40) --
  `SELECT *` and `INSERT OR REPLACE` of one whole row.
- **Zero JOINs, zero recursive CTEs, zero views, zero triggers** in the entire codebase. The
  two `grep -i` hits for "join" are prose in comments (`TrackingTools.cs:126`,
  `McpResultBuilder.cs:946`) -- re-verified 2026-07-27, do not let a naive grep talk you out
  of this.
- `work_items.fields_json` is **a document inside a relational cell** already
  (`SqliteCacheStore.cs:161`), as are `process_types.states_json` and
  `valid_child_types_json`. Seven fields are promoted to columns; the rest is an opaque bag.
- No query ever projects a subset of columns. **The unit of read is invariably the whole
  item.**

"Adopt a document model" would therefore change a label, not a behaviour. The ticket's own
premise -- *"the store is barely relational already"* -- is correct, and its conclusion is
that there is nothing to migrate *to*.

### 2. Files-on-disk is rejected

Not on taste -- on the ledger. **7 of 12 assumptions break** (A2 WAL concurrency, A3 the
5-table publish transaction, A4 set-based UPDATE/DELETE, A5 server-side `NOT EXISTS`, A6
engine ordering, A7 `AUTOINCREMENT` monotonicity, A8 single-file corruption identity), 2 are
partial, 3 are non-issues.

The decisive detail is *which* break. The three things relational genuinely earns --
the orphan `NOT EXISTS` anti-join (`SqliteWorkItemRepository.cs:212-218`), the phantom-dirty
cross-table anti-join as one atomic UPDATE (`:371-376`), and navigation cursor ordering over
`AUTOINCREMENT` with ring trim (`SqliteNavigationHistoryStore.cs:61,80,104`) -- are **exactly
the three that require a global index the indexer would have to build and maintain**. Files
would buy a format twig must then re-index to get back what it already has for free.

`ExportedWorkItem` / `WorkItemExportFormat` prove less than they appear to. They prove *one
item's field bag* round-trips losslessly to tested markdown carrying id and revision. They do
not prove the *store* round-trips: the format excludes 12 system fields by design
(`WorkItemExportFormat.cs:26-41`) and carries no `parent_id`, `is_dirty`, `is_seed`,
`last_synced_at`, no pending changes, no links, no seed metadata. **It is an editing view, not
a persistence format**, and promoting it to storage would mean growing back everything it
deliberately drops.

**Files-on-disk for LLM consumption is a separate, live idea and is NOT rejected here**
(owner, 2026-07-27). A work-item file that is a *rendered projection* -- generated on demand,
disposable, no durability contract -- carries none of the 7 broken assumptions, because it
stores nothing. That is an output-surface question adjacent to 0010, not a persistence one,
and it comes with its own real risk: a file dump is as likely to blow an LLM's context as to
help it, so it needs scoping rather than adoption. Recorded in the map's **Not yet
specified**, deliberately not ticketed here.

### 3. The decision: split the store by durability class

**0001 §1 is the constraint that actually binds:** *"disposable remote mirror"* and *"durable
local drafts"* must not share a schema. They share one today, and
`pending_changes.work_item_id -> work_items(id)` (`SqliteCacheStore.cs:174`) -- **the single
FOREIGN KEY in the whole schema** -- is the documented root cause of #268/#269/#270/#271 and
the ID-space half of #280.

So:

- **`.twig/{org}/{project}/twig.db` stays the disposable mirror.** Keeps drop-and-recreate on
  `SchemaVersion` mismatch. That behaviour is *correct* for a cache and becomes safe once it
  holds nothing irreplaceable.
- **`.twig/{org}/{project}/pending.db` becomes the durable store.** Never dropped. Gets real
  migrations (ALTER + backfill). Holds everything twig owns.

The Connection scoping is free: `TwigPaths.GetContextDbPath` (`TwigPaths.cs:85-88`) already
lays the DB out per `{org}/{project}`, so a sibling file needs no new addressing scheme and
lands exactly on 0004's per-Connection reconciliation unit.

**The FK disappears by construction.** Two tables in two different database files cannot
declare a foreign key between them. The rule that four XML doc comments have been enforcing by
prose stops needing enforcement, because the thing it was guarding against becomes
unexpressible. This is the deletion test passing on a constraint rather than a module.

### 3a. The durability test: **can ADO rebuild it?**

The line is drawn by one question, not by an enumeration (owner, 2026-07-27), so it keeps
working as tables are added:

**Durable** (`pending.db`) -- ADO does not know this exists:
- staged seeds (they exist nowhere else -- a `SchemaVersion` bump destroys unpushed work today)
- the pending set: staged notes, staged field edits
- `publish_id_map`, re-keyed by `StagedIdentity` (0003 §3 required it survive a cache wipe)
- the durable intent record (0001 §4 / 0004 §4)
- **Benches** -- a named, user-authored selection ADO has never heard of

**Disposable** (`twig.db`) -- ADO is the source of truth: `work_items` (published),
`process_types`, `field_definitions`, `work_item_links`, `context`, `metadata`, and
`navigation_history` (a UI convenience regenerated by using the tool).

**Deleted outright:** `sprint_iterations` and `area_paths` -- declared, dropped, and read by
nothing (`SqliteCacheStore.cs:250,256`; repo-wide grep hits only that file). `tracked_items`
and `excluded_items` follow once the one-shot tracking migration is retired.

Benches meet the test the same way `tracking.json` did. That subsystem **already left SQLite
for a file** (`FileTrackingRepository`, `TwigServiceRegistration.cs:109`) and kept every
behaviour -- a completed natural experiment in the repo, for lower-value data than this.

### 4. Cross-store transactions survive -- measured, not assumed

The strongest objection to splitting is that `SeedPublishOrchestrator.cs:237-279` -- the only
`IUnitOfWork.BeginAsync` call site in the product -- spans 5 tables in one commit, and the
split puts those tables in two files.

**Spiked on 2026-07-27 against `Microsoft.Data.Sqlite` under twig's actual pragmas**
(`journal_mode=WAL`, `busy_timeout=5000`, per `SqliteCacheStore.cs:73-82`). `ATTACH DATABASE`
plus a single `BeginTransaction`:

```
--- journal_mode requested: WAL ---
  effective: main=wal, attached=wal
  cross-file txn spans both: True (commit succeeded)
  cross-file rollback: main leftover=0, pending leftover=0 (0/0 = rollback spans both)
```

**One transaction spans both files, and rollback undoes both.** The publish path keeps its
semantics; `SqliteUnitOfWork` (`SqliteUnitOfWork.cs:19-43`) and the ambient-transaction
plumbing keep working, with `ATTACH` issued at open time next to the existing pragmas.

What is genuinely lost is narrower: **crash atomicity between the two file commits.** SQLite
cannot provide a master journal under WAL, so a crash in that window can leave the two files
disagreeing. This is accepted, for two reasons:

1. Twig **already** has a strictly worse version of this window -- the ADO create at step 7 is
   outside the transaction that rolls back at step 10d, which is #270. The local half was never
   the weak link.
2. 0001 §4 already ruled the fix: **record intent durably before the call**, then reconcile an
   intent with no outcome on restart. That machinery covers the ATTACH window as a side effect
   of covering the ADO window, which is the one that actually loses data.

The spike was a throwaway; it is not part of this change.

### 5. Existing data: clean break, with a guard

**No migration is written** (owner, 2026-07-27). Twig is pre-1.0 with an effectively
single-user population, and migration machinery here would be built for a population of one.
This follows the `--no-refresh` precedent from 0004 §3: take the break while it is cheap.

**But the break must be loud, not silent.** `twig init` and the version-mismatch path **must
refuse to proceed when the old `twig.db` holds a non-empty pending set**, and print push-or-
discard advice instead. A clean break that tells you is honest; one that silently eats staged
notes is #271 recurring -- *a healthy-cache rebuild that destroys unpushed work is the exact
failure this whole map exists to remove.* This guard is not optional and is not a nicety.

### 6. What this closes, and what it costs

Closes, by construction rather than by discipline:
- the single FK, and with it the shared root cause of **#268/#269/#270/#271**
- **#280** as a class -- 0003 ruled the #285 fix incomplete because `publish_id_map` lived in
  a droppable store; it now lives in one that is never dropped
- `SchemaVersion` bumps and `twig init --force` stop being data-loss events

The cost, stated plainly: **twig acquires a real migration system it can never take back.**
`pending.db` can never be dropped-and-recreated, so every future shape change to it needs
ALTER + backfill. That machinery -- not the schema change -- is the genuinely new engineering
in this decision, and it is why the sequencing below puts it first and alone.

### 7. Sequencing -- three follow-on tickets, in forced order

0005 is a decision, not an implementation. The inherited work graduates into tickets rather
than being carried here, because "implement the persistence model" is larger than one session
and would silently become a second map:

1. **0013 -- Split the store by durability class.** `pending.db`, the migration machinery, the
   `ATTACH` wiring, the clean-break guard, the FK deleted, the two dead tables removed.
   Everything else depends on it. *Blocks 0014, 0015.*
2. **0014 -- `StagedIdentity` on the durable store.** 0003's model, implemented now that there
   is somewhere durable to key it: ULID/GUIDv7, `publish_id_map` re-keyed, the persisted
   negative-int display alias (0003 §5a -- never a key, never joined on, never a FK target,
   never recycled). Retires `ISeedIdCounter` and `GetMinSeedIdAsync`, and retires the #285
   union query, which 0003 said must stay until exactly this lands. *Blocked by 0013.*
3. **0015 -- The durable intent record.** 0001 §4's record-before-the-call, keyed by
   `StagedIdentity`, closing the 7->10 window that #270 falls through. **It is the record 0003
   and 0004 both required -- not a second one.** Needs the idempotency-key mechanism, which is
   still undecided (see §8). *Blocked by 0014.*

**`SaveBatchProtectedAsync` is deliberately NOT in this chain.** It reduces skipped IDs to a
count (`SyncCoordinator.cs:168`), discarding the remote-side input `ConflictResolver` needs.
That is a hard blocker for 0006 and needs no new store -- it only needs to stop throwing away
what it saw. **It belongs to 0006**, and putting it behind the schema chain would block 0006
on work it does not need.

### 8. `SaveCommand` / `twig save` -- recorded here because it is written down nowhere else

**Keep the handler** (owner, 2026-07-27; asked and left unanswered four times before this).

`twig save` is `[Hidden]` (`Program.cs:815`), prints a deprecation hint, and dispatches to
`SaveCommand` -- which supports **per-item and all-dirty scoping that `twig sync` has no
parameter for** (`SyncCommand` takes only `(output, force, pullOnly)`). It is not a duplicate
command; it is a capability `sync` lacks, and it matches 0001 §3a's per-item selective push.

**Delete `twig save` once `twig sync <id>` exists.** That is a sequencing item on the
reconciliation module's implementation, not an open product question. It is now closed.

### 9. Further research and prototyping: one open question, and it is not this ticket's

Assessed across the three follow-on tickets (owner asked, 2026-07-27):

- **0013 (store split) -- no research needed.** The one genuine unknown was cross-file
  transaction behaviour, and §4 measured it. Straight implementation.
- **0014 (`StagedIdentity`) -- no research needed.** 0003 decided the model, both engineering
  calls are resolved (§5/§5a of that ticket), and .NET 9+ ships `Guid.CreateVersion7()`, so
  even the generator is off-the-shelf.
- **0015 (intent record) -- HAS a genuine open question, inherited not created.** 0001 §4
  requires an **idempotency key** so twig can ask ADO "did my create already land?", and
  records it as *verified absent today* -- nothing is stamped on create
  (`AdoRestClient.CreateAsync:113`). The mechanism (tag, custom field, or ADO-side dedupe) is
  undecided and depends on `%TEMP%\twig-review\research-ado-batch-push.md`, which 0001 §3a
  already pointed at. **This is 0015's own first question**, not a separate research ticket --
  it is unanswerable in the abstract and answerable in ten minutes with the ADO API in front
  of you.

**No new research or prototype ticket is warranted.** The 522-line evidence file did the
research this decision needed, and the one remaining unknown is scoped inside the ticket that
consumes it. Charting a research ticket for it would be fog-slicing -- pre-cutting a question
that its own implementation ticket answers better.
