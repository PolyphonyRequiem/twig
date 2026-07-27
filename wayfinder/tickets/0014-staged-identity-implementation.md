---
id: 0014
title: StagedIdentity on the durable store
type: task
status: closed
blocked_by: [0013]
---

## Question

Implement 0003's seed identity model, now that 0013 has given it somewhere durable to live.
0003 decided the model and explicitly assigned implementation to 0005, which sequenced it here.

Scope:

- `StagedIdentity` as a value object minted at seed creation -- **ULID/GUIDv7, not plain GUID**
  (0003 §5, owner-confirmed, stands: sortable, so creation order survives without a separate
  sequence column). .NET 9+ ships `Guid.CreateVersion7()`.
- `work_items` (or its durable-store successor) gains the staged-identity column.
- **`publish_id_map` re-keys** to `StagedIdentity -> ADO id`, keyed on something a cache
  rebuild cannot invalidate.
- **The negative-int display alias is PERSISTED** -- a durable column on the seed row, minted
  once at creation, stable across sessions so a script can reference it (0003 §5, owner veto
  of the original per-view call). Surviving constraints from 0003 §5a: **never a key, never
  joined on, never a FK target, never recycled** -- a discarded seed's alias is retired, not
  reissued.
- **Retire `ISeedIdCounter` and `GetMinSeedIdAsync`.** 0003 §3 predicted both delete cleanly;
  that is the deletion test and it is the signal the abstraction carried accidental complexity.
- **Retire the #285 union query.** 0003 §5 said it stays in place *until this lands* -- it is
  correct for the paths it covers and removing it early reopens #280.

No new decisions expected: 0003 settled the model and both of its vetoable engineering calls
are resolved.

**Owns the suite.**

## Answer

**Built as specified by 0003. No decision reopened.** `StagedIdentity` is a GUIDv7 value object
minted at seed creation; the negative integer is a persisted display alias hanging off it;
`publish_id_map` re-keys to the identity; and `ISeedIdCounter`, `GetMinSeedIdAsync` and the #285
union query are all deleted. Suite green, exit 0 on all four projects.

### 1. The design question the ticket left open: where the durable identity lives

The handoff flagged the real problem — seeds are not a separate table, they are rows in
`work_items` with `is_seed = 1`, and `work_items` is in the **disposable mirror**. A durable
identity on a droppable row is exactly the incoherence 0003 objected to.

**Resolution: a new `pending.staged_identities` table is the source of truth.**

```
staged_identity TEXT PRIMARY KEY   -- the key
alias           INTEGER NOT NULL UNIQUE
created_at      TEXT NOT NULL
retired_at      TEXT               -- NULL while live
```

`work_items` gains a `staged_identity` column too, but only as a join-free read convenience the
durable table can rebuild — it is a mirror of durable state, which is what the mirror is *for*.
The identity, the alias and the retirement record live where a `SchemaVersion` bump cannot reach
them.

Two of 0003 §5a's four constraints are now **structural rather than prose**:

- **Never a key, never an FK target.** `alias` is `UNIQUE` but deliberately not the primary key,
  and the table declares no foreign keys. `StagedIdentities_KeysOnTheIdentity_AndTheAliasIsNeverAKey`
  reads `pragma_table_info` and `pragma_foreign_key_list` and fails if either changes.
- **Never recycled.** `RetireAsync` is an `UPDATE`, never a `DELETE`. The row *is* the retirement
  record, so `MIN(alias)` cannot walk back over an issued number. Deleting it would have quietly
  reintroduced the reuse the ticket forbids.

This is also what lets §5a's persisted alias coexist with §2's rejection of allocators: the alias
floor is an allocator, but it now has a durable home *and* its output is decorative. An allocator
whose output is decorative may reuse a floor; one whose output is identity may not.

### 2. Durable migration v2 — additive, per 0013's rule

`DurableSchemaVersion` 1 → 2, one new ledger entry: `CREATE TABLE staged_identities`, plus
`ALTER TABLE publish_id_map ADD COLUMN staged_identity`, plus a backfill that mints a synthetic
identity for every pre-0014 mapping row so none becomes unreachable. No drop, no recreate.
`SchemaVersion` (the mirror) 11 → 12 for the new `work_items` column, which is safe precisely
because the mirror is droppable.

`DurableStore_UpgradingFromV1_AddsTheIdentityShape_WithoutDroppingExistingRows` exercises the
upgrade against a store that already exists, not the create-at-v2 happy path — the create path
would never have caught an `ALTER` mistake.

### 3. The deletion test passed — and found two live bugs while doing it

0003 §3 predicted `ISeedIdCounter` and `GetMinSeedIdAsync` would delete cleanly. They did, along
with `SeedIdCounter`, `SeedFactory.InitializeSeedCounter`, and the #285 union query.

**0003 §2's argument was not hypothetical — there were already TWO sixth-call-sites on `main`:**

- `NewCommand` (`twig new`)
- `CreationTools` (MCP `twig_new`)

Both create seeds. **Neither ever called `Initialize`.** Neither appeared in the handoff's verified
20-file list, because neither *referenced* the counter — which is precisely the failure mode: the
preamble is invisible by omission. Both were silently issuing IDs from an uninitialised counter,
i.e. reissuing from zero. This is the same shape as 0008's `SaveCommand` find and 0013's
`init --force` find: the ticket that removes the mechanism is the one that discovers who was
already misusing it.

The new shape makes it unforgettable rather than guarded. `identity` is a **required parameter** on
both `SeedFactory` methods, so a future seventh call site is a compile error, not a silent
collision. That is the difference between deleting an abstraction and guarding one.

### 4. `publish_id_map` re-keyed

`IPublishIdMapRepository` now keys on `StagedIdentity`. `GetAllMappingsAsync` returns
`PublishMapping(Identity, Alias?, NewId)` in place of the `(int OldId, int NewId)` tuple.

One alias-shaped read survives deliberately: `twig history` starts from a number a *user typed*.
`GetNewIdByAliasAsync` resolves that through the durable register first and returns `null` for an
unknown alias rather than matching a neighbour — 0003 §4's rule that twig does not coerce an
unknown value into a plausible known one. `SeedReconcileOrchestrator` similarly builds its lookup
from `Alias` because `seed_links` stores the alias; that is display-side resolution, not a join.

### 5. Test cost, measured

Baseline at `0d0b1cce`: **7,389 passing, exit 0**. After: **7,379 passing, exit 0**
(Cli 2883 / Infra 1355 / Mcp 1313 / Domain 1828).

The net −10 is accounted for, not drift: `SeedIdCounterTests` (12 counter-arithmetic facts) was
deleted with the type; four `SeedIdCounter_*` facts in `WorkItemTests` and two counter-init facts
in `SeedFactoryTests` were **repurposed** into `StagedAlias`/`StagedIdentity` properties rather
than dropped; one MCP test that stubbed the deleted `GetMinSeedIdAsync` was removed; and five new
schema/identity guards were added.

**Two completeness guards fired, exactly as designed** — 0013's
`Schema_PlacesEveryTableInExactlyOneStore_ByDurability` and the mapper's
`Map_PopulatesAllInitOnlyProperties`. Both demanded the new table/property be declared. That is
the guards working, and both inventories were updated rather than relaxed.

`WorkItemBuilder.AsSeed()` now mints an identity by default. A real seed always carries one, so a
fixture without it would silently skip the publish path's identity-keyed branch — the hollow-fixture
trap `AGENTS.md` warns about.

**Non-vacuity verified** against a detached worktree at `0d0b1cce`: the three schema guards fail
there, and the `StagedAlias`/`StagedIdentity` tests do not even compile — the types do not exist.

### 6. What this closes, and what it unblocks

- The #280 failure **class**, at the model level this time rather than by relocating a floor.
  0013 put `publish_id_map` beyond a `SchemaVersion` bump; 0014 makes its key non-reissuable.
- The five-call-sites-must-remember problem, by deleting the thing they had to remember.
- **Unblocks 0015** (the durable intent record), which 0003 §3 noted needs a key that exists
  before ADO assigns one and survives a cache wipe. `StagedIdentity` is that key. Not started.
