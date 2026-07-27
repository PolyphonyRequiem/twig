---
id: 0013
title: Split the store by durability class
type: task
status: open
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

<!-- empty until resolved -->
