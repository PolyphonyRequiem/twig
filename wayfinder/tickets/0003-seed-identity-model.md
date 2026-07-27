---
id: 0003
title: Seed identity model
type: grilling
status: closed
blocked_by: [0001]
---

## Question

Should a seed's identity remain a recycled negative integer, or become a durable `StagedIdentity`? Issue #280 proves the current model is broken: `SeedPublishOrchestrator.cs:265-266` deletes the published seed row, `SqliteWorkItemRepository.cs:282` re-seeds the counter from `MIN(id) FROM work_items WHERE is_seed=1`, so `SeedIdCounter.cs:14` re-issues IDs that `publish_id_map` still treats as permanent keys. Three options: (a) durable high-water mark for the allocator, (b) seed the counter from both tables, (c) `StagedIdentity` value object, leaving the negative int as a display concern. (a) is smallest; (c) makes the bug class impossible. This decision also governs the #268/#269/#270 family, so decide it once rather than patching twice.

## Answer

**A seed's identity becomes a durable `StagedIdentity` minted at creation. The negative
integer is demoted to a display alias.** Options (a) and (b) are rejected as a class, not
individually: both are *allocators*, and every allocator needs a durable floor that twig
has nowhere to keep.

### 1. The ticket's premise is stale — (b) already shipped, and it is not enough

PR #285 (`bf8a26aa`) landed option (b). `SqliteWorkItemRepository.GetMinSeedIdAsync`
(`:278-299`) now derives the floor from both tables:

```sql
SELECT MIN(id) FROM (
    SELECT id     FROM work_items     WHERE is_seed = 1
    UNION ALL
    SELECT old_id FROM publish_id_map
);
```

That closes the reported bug. It does **not** close the bug class, because it relocates the
allocator's correctness onto an assumption that is false in this codebase: that
`publish_id_map` is permanent.

It is not. `SqliteCacheStore.DropAllTables()` (`:114`) lists `publish_id_map` among the
tables it drops, and `EnsureSchema()` (`:83-91`) calls it on **any** schema-version
mismatch — `SchemaVersion` is currently `10` and has moved before. `twig init --force`
deletes the DB file outright (`InitCommand.cs:191`). Either path resets the floor to zero
while an already-published seed's negative ID remains a live key in *other* people's
records of it — links, notes, anything reconstituted later — and the next seed created
reissues it. Same #280 failure, reached by a different door.

This is 0001 §1 restated: `publish_id_map` is **durable provenance living in the
disposable mirror's table set**. 0001 already ruled those must not share a schema. 0003 is
where that ruling becomes concrete, and honouring it is precisely what kills option (b) —
once `publish_id_map` moves to durable storage, the union query is a cross-store join, and
the "cheap" option stops being cheap.

### 2. An allocator whose correctness depends on five callers is the wrong shape

`ISeedIdCounter.Initialize` is invoked from **five** sites, each independently responsible
for querying the right floor first:

- `SeedFactory.cs:20` (via `InitializeSeedCounter`)
- `SeedNewCommand.cs:122-124`
- `SeedChainCommand.cs:84-86`
- `SeedTools.cs:46-48` (MCP `twig_seed_new`)
- `SeedTools.cs:490-492` (MCP `twig_seed_chain`)

The counter itself is in-memory and per-process (`SeedIdCounter.cs:11-18`), re-derived from
a DB query every run. A sixth entry point that forgets the two-line preamble compiles
cleanly and silently issues colliding IDs — the exact failure shape ticket 0008 found in
`SaveCommand` and answered with completeness guards. Guarding this one the same way is
possible, but it guards a mechanism that should not need guarding: **a generated identity
has no preamble to forget.**

### 3. What `StagedIdentity` is

A value object minted at seed creation, self-contained and collision-free without
consulting existing state (ULID/GUIDv7 — sortable, so creation order survives). It carries
its own display alias; the negative integer becomes a *rendering* concern, allocated per
view, never persisted as a key and never joined on.

Consequences that make the bug class impossible rather than guarded:

- **No floor, no scan, no `Initialize`.** `ISeedIdCounter` and `GetMinSeedIdAsync` both
  delete cleanly — the deletion test passes, which is the signal the abstraction was
  carrying accidental complexity.
- **`publish_id_map` becomes `StagedIdentity → ADO id`**, keyed on something a cache
  rebuild cannot invalidate. It can then move to durable storage per 0001 §1 without a
  cross-store join, because nothing reads it to allocate.
- **Recovery becomes addressable.** 0001 §4 requires recording intent *before* the ADO
  call so a crash in the 7→10 window is reconcilable. That intent record needs a key that
  exists before ADO assigns one and survives a cache wipe. A negative int allocated from a
  table that may be dropped cannot be that key; `StagedIdentity` can. **0003 is a
  precondition for 0001 §4, not merely consistent with it.**
- **It crosses the 0002 seam as a type.** Per 0002, mutation workflows return outcome
  unions matched by each surface. Seed identity appears in those outcomes, so
  `StagedIdentity` becomes part of a seam contract — which is an argument for a real type
  over a bare `int`, not against it.

### 4. Relationship to #286

#286 (unrecognized states silently bucketed as `Proposed`) is the same failure *mode* in
the state half of the model: an unknown value is silently coerced into a specific,
probably-wrong known value instead of staying visibly unknown. Identity had the mirror
version — a recycled ID silently resolved to a previous owner. The shared rule this ticket
sets: **twig does not coerce an unknown or absent value into a plausible known one.** #286
remains a separate fix; it is not in this ticket's scope, but it should be fixed under that
rule rather than by extending a lookup table.

### 5. Scope and sequencing

Decision only — no code moved in this ticket. Implementation is a schema change
(`SchemaVersion` bump, `work_items` gains a staged-identity column, `publish_id_map`
re-keys) plus a migration for existing seeds, and it is entangled with 0005 (persistence
model). **0005 owns the implementation; 0003 fixes the model it must implement.** The #285
union query stays in place until then — it is correct for the paths it covers and removing
it early would reopen #280.

Two calls made on engineering grounds, open to veto:

- **ULID/GUIDv7 over plain GUID** — sortable, so "first seed created" stays answerable
  without a separate sequence column.
- **The negative-int alias is allocated per view, not persisted.** Persisting it would
  reintroduce a durable allocator through the back door.
