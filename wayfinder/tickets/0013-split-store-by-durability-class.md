---
id: 0013
title: Split the store by durability class
type: task
status: closed
blocked_by: [0005]
---

## Question

Implement 0005's decision: `.twig/{org}/{project}/twig.db` stays the **disposable mirror**
(drop-and-recreate on `SchemaVersion` mismatch is retained and becomes safe), and a sibling
`pending.db` becomes the **durable store** that is never dropped.

Scope:

- Create `pending.db` alongside the mirror. `TwigPaths.GetContextDbPath`
  (`TwigPaths.cs:85-88`) already scopes per `{org}/{project}`, so the addressing exists.
- **Migration machinery for the durable store.** ALTER + backfill, versioned. This is the
  genuinely new engineering -- `pending.db` can never be dropped-and-recreated, so it needs
  a real migration path forever. Twig has had **zero** migrations to date
  (`SqliteCacheStore.cs:86-92`).
- Wire `ATTACH DATABASE` at open time, next to the existing pragmas
  (`SqliteCacheStore.cs:73-82`). 0005 §4 measured that one transaction spans both files and
  rollback undoes both under WAL, so `SqliteUnitOfWork` (`SqliteUnitOfWork.cs:19-43`) and the
  5-table publish transaction (`SeedPublishOrchestrator.cs:237-279`) keep their semantics.
- Move to the durable store per 0005 §3a's **"can ADO rebuild it?"** test: staged seeds, the
  pending set, `publish_id_map`, Benches. Everything ADO can rebuild stays in the mirror.
- **Delete the single FOREIGN KEY** (`SqliteCacheStore.cs:174`). It becomes unexpressible once
  the two tables live in different files -- which is the point. The four XML doc comments that
  enforce its ordering rule by prose go with it.
- **Delete the dead tables** `sprint_iterations` and `area_paths` (`:250`, `:256`) -- declared,
  dropped, read by nothing.
- **The clean-break guard (not optional).** No data migration is written (0005 §5), so
  `twig init` and the version-mismatch path **must refuse to proceed when the old `twig.db`
  holds a non-empty pending set**, printing push-or-discard advice. A silent break here is
  #271 recurring: a healthy-cache rebuild that destroys unpushed work.

Closes the shared root cause of #268/#269/#270/#271, and closes #280 as a class by moving
`publish_id_map` somewhere a `SchemaVersion` bump cannot drop it.

**Owns the suite.** This is a schema change, not docs -- see `AGENTS.md`, run the four test
projects serially with `-m:1`, and trust the exit code, not the summary line.

## Answer

**Built and merged as specified by 0005.** `pending.db` exists, is ATTACHed as schema `pending`,
holds the durable tables, and is never dropped. The FK is gone. The clean-break guard refuses
instead of warning. Suite green on all four projects.

### 1. What the split actually looks like

`SqliteCacheStore` opens the mirror, then ATTACHes a sibling `pending.db` as schema `pending`
next to the existing pragmas (`SqliteCacheStore.cs`, `AttachDurableStore`). Placement:

| `pending.db` (durable, never dropped) | `twig.db` (mirror, drop-and-recreate) |
|---|---|
| `pending_changes` | `work_items`, `process_types`, `field_definitions` |
| `publish_id_map` | `work_item_links`, `context`, `metadata` |
| `seed_links` | `navigation_history`, `tracked_items`, `excluded_items` |

`sprint_iterations` and `area_paths` are deleted outright.

**Two facts a later ticket needs:**

1. **SQLite resolves an unqualified table name across every attached schema.** So *no repository
   SQL changed* — `SELECT * FROM pending_changes` finds the durable table with no prefix. The
   cross-store anti-joins in `SqliteWorkItemRepository.GetMinSeedIdAsync` and
   `ClearPhantomDirtyFlagsAsync` also keep working verbatim across the two files.
2. **The durable path is derived inside the constructor**, not passed in. All three construction
   sites (`Program.cs`, `TwigServiceRegistration.cs`, `WorkspaceContextFactory.cs`) and ~60 test
   sites needed **zero** changes. This deliberately sidesteps the 0008 failure shape: there is no
   fourth place to forget, because there is no second parameter. `TwigPaths` gained no
   `PendingDbPath` — the handoff anticipated one, and it turned out to be unnecessary surface.
   An in-memory mirror gets an in-memory durable store, so tests need no second path.

### 2. The migration machinery — the genuinely new engineering

The durable store is versioned by `PRAGMA pending.user_version`, **independently of
`SchemaVersion`**, and upgraded by an ordered ledger (`DurableMigrations`) applied inside one
transaction. Currently one entry, version 1.

**The rule this establishes, and it can never be taken back:** every future shape change to
`pending.db` is an additive `ALTER` + backfill entry in that ledger plus a
`DurableSchemaVersion` bump. It cannot be a drop-and-recreate. A bump with no matching migration
throws rather than silently skipping.

### 3. The FK is deleted, and its prose enforcement with it

Two tables in two files cannot declare a foreign key, so
`pending_changes.work_item_id -> work_items(id)` is unexpressible now, not merely removed. The
three doc comments that enforced its ordering rule by prose
(`IPendingChangeStore.RemapWorkItemIdAsync`, `SeedDiscardOrchestrator:125`,
`SeedPublishOrchestrator:250,256`) were rewritten to record that the ordering is now **intent,
not obligation**. The orchestrator sequencing itself is unchanged — clearing/migrating staged
rows is still correct, for the data-preservation reason, not the constraint reason.

`SchemaVersion` bumped 10 → 11.

### 4. The clean-break guard — and the live bug it exposed

Non-optional, and it needed **two** enforcement points, not one:

- **`SqliteCacheStore.GuardLegacyPendingSet`** — a version-mismatch rebuild throws with
  push-or-discard advice when the legacy `main.pending_changes` is non-empty, before dropping
  anything.
- **`InitCommand`** — this is the find. `init --force` printed *"⚠ Pending changes exist and will
  be lost"* and then **deleted the database anyway** (`InitCommand.cs:185-191`). That is #271
  exactly: a healthy-cache rebuild that destroys unpushed work, with a warning the user cannot
  act on because the deletion already happened. It now **returns exit 1 and refuses**, and
  deletes only the mirror — `pending.db` is never removed by `--force`.

**A second real bug surfaced from the ordering:** an empty legacy `main.pending_changes` would
**shadow** the durable table, because unqualified names resolve against `main` first — silently
routing every staged write back into the droppable store. `DropLegacyDurableTables` removes the
stale copies. Both bugs were caught by an existing test failing, not by inspection.

### 5. Cross-file transactions: unchanged, as 0005 §4 measured

`SqliteUnitOfWork` and the 5-table publish transaction were not touched and did not need to be.
`SeedPublishTransactionIntegrationTests` passes unmodified — the strongest available evidence
that one `BeginTransaction` still spans both files.

One operational note found in the build: **`Microsoft.Data.Sqlite` pools connections by
connection string, and a pooled connection retains its ATTACHes.** Re-attaching throws
`database pending is already in use`, and a stale attach can point at a deleted file. The store
DETACHes first if already attached.

### 6. Test cost, measured

Baseline before any change: **7,383 passing, exit 0** across all four projects. After:
**7,389 passing, exit 0** (+6 net).

The churn was far smaller than the ~20 files the handoff projected — **4 test files** (5 source files), because
no repository SQL changed. Three pre-existing tests asserted the FK behaviour this ticket
deletes, and were **inverted rather than removed**, since the invariant genuinely flipped:

- `SeedPublishWithStagedNoteTests.Fixture_ForeignKeyIsGone_SoTheFailureClassIsUnexpressible`
- `SeedDiscardWithStagedNoteTests.Fixture_ForeignKeyIsGone_SoTheFailureClassIsUnexpressible`
- `SeedPublishWithStagedNoteTests.PublishSeed_WithStagedNote_WithoutPendingChangeStore_NoLongerThrows`
  — the #270 duplicate-creation trap is closed: the publish now completes and `publish_id_map`
  lands, so a retry cannot create a second ADO item.

New guards: `Schema_PlacesEveryTableInExactlyOneStore_ByDurability` is the completeness guard in
0008's spirit — a new table in the wrong store fails there rather than silently becoming
droppable durable state. Plus the two clean-break guard tests and the two `init --force` tests.

**All six new/changed tests were verified non-vacuous** against a detached worktree at `0615dcd5`
(one symbol reference stubbed to make the file compile there): all six fail on the unfixed code.

`CorruptionRecoveryTests` needed no change. The A8 single-file corruption identity concern the
handoff flagged is real but did not bite: corruption detection is per-connection at open, and the
mirror is still the file that gets deleted.

### 7. What this closes

- The single FK, and with it the shared root cause of **#268/#269/#270/#271**.
- **#280** as a class — `publish_id_map` now lives somewhere a `SchemaVersion` bump cannot reach.
- `SchemaVersion` bumps and `twig init --force` stop being data-loss events.

Individual issue fixes stay as-is; this removes the root cause, not the symptoms.

**Unblocks 0014** (`StagedIdentity` on the durable store) and, behind it, 0015. Neither was
started: no identity minting, no intent record. `Bench` still has no repository, so nothing
Bench-shaped moved — when it lands it goes in `pending.db` per §3a.
