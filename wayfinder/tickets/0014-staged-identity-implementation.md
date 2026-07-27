---
id: 0014
title: StagedIdentity on the durable store
type: task
status: open
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

<!-- empty until resolved -->
