---
id: 0005
title: Persistence model
type: research
status: open
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

<!-- empty until resolved -->
