---
id: 0006
title: Baseline revision for three-way merge
type: grilling
status: closed
blocked_by: [0001, 0004]
---

## Question

Should twig persist a baseline revision per work item to enable three-way merge?
`ConflictResolver` currently makes a two-way guess and documents its own limitation at
`ConflictResolver.cs:117-120`. The audit's assessment: one persisted baseline-revision
integer converts it to a three-way merge — the highest leverage-per-unit-of-interface
change found in the entire review.

## Retitled and re-scoped (0001, 2026-07-26)

**Was: "Team-scale baseline revision."** The old framing said this "only pays off if 0001
answers *shared substrate*." **0001 answered the opposite** — twig is a single-user local
tool, and the shared substrate is ADO, never twig.

That does **not** kill this ticket; it corrects why it matters. Twig already has a second
writer at N=1 users, and it is **ADO**. A baseline revision enables three-way merge between
*local edits* and *remote changes*, which is a single-user concern. Nothing about it was
ever team-scale — the title was wrong, not the idea.

Supporting evidence from the ADO research (0001 §7): updates fenced by a JSON-Patch `test`
op on `/rev` are replay-safe, which means the remote side of the three-way merge has a
usable revision fence. `System.Rev` is reliably returned on reads. So the mechanism this
ticket needs is available — full findings in
`%TEMP%\twig-review\research-ado-batch-push.md`.

Still blocked by 0004: a baseline only means something once reconciliation is a named
module that owns when comparison happens.

## Answer

**No — do not persist a baseline revision. The baseline already exists, it is already
durable, and it is already finer-grained than the integer the audit asked for.** It is
`pending_changes.old_value`. The audit found the right problem and priced the wrong fix: the
gap is not missing state, it is that `ConflictResolver` is never handed the state twig
already keeps.

### 1. What the two-way guess actually gets wrong — and it is worse than "two-way"

`ConflictResolver.CompareProperty` (`ConflictResolver.cs:108-121`) flags **any** divergence
between `local` and `remote` as a conflict, and says so in its own comment
(`ConflictResolver.cs:117-120`): *"Without a shared baseline revision we cannot determine
which side changed, so we conservatively flag any divergence."*

The real defect is one level below that. **The `local` argument does not contain the local
edit.** Staging never writes the edited value onto the cached aggregate:

- `EditCommand.StageLocallyAsync` (`EditCommand.cs:203-210`) writes each `FieldChange` to
  `pendingChangeStore.AddChangeAsync(item.Id, "field", …)` and then mutates exactly one
  thing on the item — `item.UpdateField("_edited", "true")`. The user's new Title/State/
  AssignedTo is never applied.
- The TUI does the same (`WorkItemFormView.cs:240`), batching to
  `AddChangesBatchAsync` and caching the values in a view-local `_savedEdits` dictionary.
- `NoteWorkflow.StageLocallyAsync` (`NoteWorkflow.cs:93-102`) stages the note and appends a
  `PendingNote`, which `ConflictResolver` does not read at all.

It could not be otherwise: `Title`, `AssignedTo`, `IterationPath`, `AreaPath` and `ParentId`
are `init`-only on the aggregate (`WorkItem.cs:30-33`), so there is no mutator to apply an
edit through.

So `Resolve(local, remote)` is not comparing local-vs-remote. **It is comparing the
last-synced cache mirror against fresh remote** — two snapshots of the *same* side. Both
outcomes are wrong:

- **False conflict.** Alice stages `State: Active → Resolved`. Bob changes `System.Title` in
  ADO. Remote revision advances, so the `local.Revision == remote.Revision` short-circuit
  (`ConflictResolver.cs:38`) does not fire. `CompareProperty("System.Title", …)` sees the
  stale cached title against Bob's new one and reports a conflict on **a field Alice never
  touched**. `ConflictResolutionFlow` (`ConflictResolutionFlow.cs:52-56`) then prompts her
  `Keep [l]ocal, [r]emote, or [a]bort?` about Bob's title edit — and if she answers `l` to
  protect her state change, `PendingChangeFlusher` proceeds
  (`PendingChangeFlusher.cs:135-138`) and pushes. Answering `r` calls
  `onAcceptRemote` → `ClearChangesAsync(item.Id)` (`PendingChangeFlusher.cs:127`), which
  **discards her staged state change** to resolve a conflict that was not hers. The
  interactive prompt is asking the wrong question about the wrong field.
- **Missed conflict.** Alice stages `State: Active → Resolved`; Bob sets the same field to
  `Closed` in ADO. Cached local state is `Active`, remote is `Closed` — they differ, so this
  one happens to be flagged, but for the wrong reason and with the wrong values shown
  (`local='Active'` is not what Alice wants; `Resolved` is). If Bob's edit had merely
  restored `Active` after a round trip, cached local and remote would *match*,
  `CompareProperty` returns early at line 114, and Alice's genuine same-field divergence is
  reported as no conflict at all.

There is also a permanent cosmetic false positive: `_edited` exists only in local `Fields`
and never in remote, so the dictionary loop (`ConflictResolver.cs:77-81`) classifies it
`AutoMergeable` on every staged item forever.

### 2. Where the baseline lives — it is already in the durable store

`PendingChangeRecord` is `(WorkItemId, ChangeType, FieldName, OldValue, NewValue)`
(`PendingChangeRecord.cs:6-11`), persisted as `old_value` / `new_value` columns
(`SqliteCacheStore.cs:293-301`) in the **durable** `pending` schema created by 0013's
migration ledger. `OldValue` is captured at the instant of edit from the value the user was
looking at — `EditCommand.cs:99-108` reads `item.Title` / `item.State` / `item.AssignedTo`
before overwriting, and `WorkItem.UpdateField` (`WorkItem.cs:88-95`) returns the old value in
the `FieldChange` it emits.

That is the merge base, per field, and it is on the correct side of **0005's durability test
("can ADO rebuild it?")** — it already lives in `pending.db`, which is never dropped. **No new
store, no new column, no new schema version.** 0013's migration ledger does not need to be
touched, which matters: 0013 called that ledger *"twig's first migration path, and one it can
never take back."*

A persisted baseline *revision integer* would be **strictly worse** on the same durability
axis. A revision number is not a merge base; it is a pointer to one. Twig caches no revision
history (the mirror holds one row per item), so resolving `baseRevision` into base *values*
requires an extra ADO round-trip per conflicting item. The audit's "one integer" would have
bought a lookup key for data twig would then have to fetch — while the values themselves were
sitting in `old_value` the whole time.

### 3. The unit is the pending-set entry, not the work item

This falls out of §2 rather than being chosen. A per-item baseline revision answers *"is the
remote ahead of me?"* — which twig already computes without one, at
`RefreshOrchestrator.cs:168` (`remoteItem.Revision > localItem.Revision`). The question that
actually decides a merge is *"did the user touch **this field**?"*, and only a per-field
record answers it. Per-entry also degrades correctly: a field with no pending row was not
edited locally, so remote wins by definition — no conflict, no prompt.

This is consistent with **0004**: the unit of *reconciliation* is the pending set per
Connection; the unit of *merge base* is one entry within it.

### 4. The actual change — an argument, not a column

```
MergeResult Resolve(WorkItem local, WorkItem remote, IReadOnlyList<PendingChangeRecord> staged)
```

Per field, with `base = staged.OldValue`, `mine = staged.NewValue`, `theirs = remote`:

| condition | outcome |
|---|---|
| no staged row for the field | remote wins — not a conflict |
| `base == theirs` | local edit applies cleanly — `AutoMergeable` |
| `base == mine` | user re-typed the same value; remote wins — `AutoMergeable` |
| `mine == theirs` | converged independently — no conflict |
| otherwise | true `HasConflicts(field, mine, theirs)` |

`HasConflicts` then reports the user's *intended* value rather than a stale cached one, which
is what makes the `[l]ocal / [r]emote` prompt answerable. The `MergeResult` union
(`ConflictResolver.cs:23`) is unchanged — no surface churn at `ConflictResolutionFlow.cs:41`
or `BatchCommand.cs:390`, and `ConflictResolver` stays pure and static.

The `local.Revision == remote.Revision` short-circuit (`ConflictResolver.cs:38`) must
**stay**, and stays correct: staging never calls `MarkSynced` (`WorkItem.cs:108-110`), so a
staged item's revision is still its last-synced one and any remote movement is visible.

**Verdict on the audit's claim.** "Highest leverage-per-unit-of-interface-change in the
review" — the leverage is real and the direction was right, but the pricing was wrong in both
terms. The unit of interface change is *smaller* than claimed (one parameter, zero persisted
state, no migration), and the leverage is *larger*: it does not merely upgrade a two-way
compare, it repairs a compare whose local side was never local.

### 5. `SaveBatchProtectedAsync` — decided here, code lands as a follow-on

0005 §7's diagnosis is confirmed: `SaveBatchProtectedAsync` returns `IReadOnlyList<int>`
(`ProtectedCacheWriter.cs:27, 54`) and every caller immediately reduces it to arithmetic —
`fetchedItems.Length - skippedIds.Count` (`SyncCoordinator.cs:168`, and again at `:189`,
`:236`). The fetched remote `WorkItem` for a protected item is dropped on the floor.

**But 0005's "hard blocker for 0006" is overstated, and this ticket is not blocked on it.**
The conflict paths that exist today all fetch their own remote directly —
`PendingChangeFlusher.cs:122`, `EditCommand.cs:113`, `BatchCommand.cs:370` — so the three-way
merge in §4 can land with `SaveBatchProtectedAsync` untouched. What it blocks is
**0004's reconciliation module**: a module that reconciles the pending set on *refresh*
cannot see the remote side, because the batch writer already discarded it. That is where the
cost lands, and it is 0004's dependency, not 0006's.

**Decision (shape only):** widen the return to carry what was seen rather than a tally —

```
readonly record struct ProtectedWriteResult(
    IReadOnlyList<WorkItem> Saved,
    IReadOnlyList<WorkItem> Skipped);
```

`Skipped` carries the *remote* items that were withheld, which is precisely the
`ConflictResolver` input being thrown away. `skippedIds.Count` becomes `Skipped.Count`, so
every existing arithmetic call site is a mechanical edit and no behaviour changes until a
consumer reads `Skipped`.

**Not expanded into code here.** It is two methods and five call sites — small, but it is
source in `Twig.Domain` plus test churn, its only *consumer* is 0004's module which does not
exist yet, and this session runs alongside four siblings under a no-source-code rule for
`type: grilling` tickets. Landing a widened return with nobody reading it would be dead
interface. **Recommendation to the owner: attach it to 0004's implementation ticket**, whose
module is the first thing that needs `Skipped`. If it is wanted sooner, it is a clean
standalone follow-on.

### 6. What this closes and what it hands on

- **No new persisted state.** 0013's `pending` schema and its migration ledger are untouched.
- **`ConflictResolver` gains a third argument**, and callers must supply
  `GetChangesAsync(item.Id)` — which `PendingChangeFlusher.cs:77` already has in hand at the
  call site, and `EditCommand` / `BatchCommand` already inject `IPendingChangeStore`.
- **Hands to 0004:** the reconciliation module owns *when* `Resolve` is called and is the
  consumer that justifies the `ProtectedWriteResult` widening in §5.
- **Regression fixtures must advance the remote revision** (`remote.MarkSynced(n)`) or line 38
  short-circuits and the branch under test never runs; assert the precondition explicitly.
  `MergeResult` is a `union` — pattern-match `result is HasConflicts`.
