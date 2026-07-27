---
id: 0015
title: The durable intent record
type: task
status: closed
blocked_by: [0014]
---

## Question

Implement 0001 §4: **record intent durably BEFORE the ADO call**, then record the outcome
after. On restart, an intent with no outcome is reconcilable -- twig can ask ADO whether the
create landed.

This closes the **7->10 window** in `SeedPublishOrchestrator`. #270 is what falls through it:
the ADO create at step 7 is outside the transaction that rolls back at step 10d, so the remote
item is orphaned and **every retry creates another duplicate with no local record**. #270
patched the FK ordering *inside* step 10; the window itself is still open.

Scope:

- The intent record lives in the durable store (0013) and is **keyed by `StagedIdentity`**
  (0014).
- **It is the SAME record 0003 §3 and 0004 §4 both required. Do not design a second one.**
- Owned by 0004's reconciliation module as the `staged -> published` transition.
- It also covers the narrower ATTACH-window risk 0005 §4 accepted (a crash between the two
  file commits), as a side effect of covering the ADO window.

### Open question this ticket must answer first

**What is the idempotency key?** 0001 §4 requires something twig stamps on creation so it can
later ask ADO *"did my create already happen?"* -- and records it as **verified absent today**:
nothing is stamped on create (`AdoRestClient.CreateAsync:113`, no tag or field written by
`SeedPublishOrchestrator`).

Candidate mechanisms: an ADO tag, a custom field, or ADO-side dedupe. Undecided, and dependent
on `%TEMP%\\twig-review\\research-ado-batch-push.md`, which 0001 §3a already pointed at.
0005 §9 judged this **answerable inside this ticket rather than as a separate research
ticket** -- it is unanswerable in the abstract and quick to settle with the ADO API in hand.

**Owns the suite.**

## Answer

**Built:** the durable intent record. `publish_intents` on the durable store (0013), keyed by
`StagedIdentity` (0014), written BEFORE the ADO call and completed after it. This is the record
0003 §3 and 0004 §4 both required -- not a second one.

### The idempotency key: a **single constant ADO tag** plus local disambiguation

The three candidates, settled with the API in hand:

- **ADO-side dedupe -- does not exist.** `research-ado-batch-push.md` §5 and §Gaps#7: creates have
  no `clientRequestId`, no dedupe token, no conditional-create. Verified absent, so
  push-and-recover is not a design preference -- the API leaves no alternative.
- **A custom field -- would require changing the owner's ADO process template.** That is an
  organisational decision, not a code one, so it was not taken and is not recommended.
- **A tag -- chosen.** Tags are per-work-item *data*, not schema. Microsoft Learn ("Add work item
  tags") records `Create tag definition` as a default Contributor permission, so this needed no
  template change and no escalation.

**The tag must be a CONSTANT, not per-create.** The first implementation stamped
`twig-intent:<GUIDv7>`, one unique tag per published item. That is wrong twice over: it grows the
project's unique-tag set without bound (ADO caps a project at roughly 5,000), and it writes
twig's private bookkeeping into a namespace **shared with every human in the project**, who then
see it in their tag picker and autocomplete. 0001 §1 already forbids exactly this -- the shared
substrate is ADO, and twig owns only the pending set. A single-user tool must not colonise a
shared vocabulary to track its own state. Caught in review before merge.

So the mechanism splits: the tag **narrows**, local state **identifies**.

- `twig-publishing` is stamped on create and **removed once the publish is recorded**, so the
  in-use set is bounded by what is actually in flight -- normally one, since publishing is serial
  and topologically ordered. Twig's permanent contribution to the project tag vocabulary is
  **one** tag, not one per item.
- The intent row carries `title`, `type_name` and `recorded_at`. Recovery queries the tag, then
  matches title + type + `System.CreatedDate >= recorded_at`. Titles can in principle collide (see the known
  limitation below), and because `recorded_at` is written *before* the call it is a sound lower
  bound -- which is what stops a reused tag matching an older item. `recorded_at` is never
  re-stamped while an intent is open, since moving the fence forward would push it past the very
  create it exists to find.

Tag shape is otherwise constrained by the docs and asserted: no leading `@` (ADO reads it as a
query macro, making the tag unqueryable -- and an unqueryable key cannot answer the one question
it exists for), no `;` or `,` (tag separators), under the 400-character cap.

Removing the tag is **best-effort**: by that point the publish has succeeded, so a failure there
must not turn a successful publish into a reported failure. A leftover tag is cosmetic.

### What closes the 7->10 window

`SeedPublishOrchestrator` step 7 now: record intent -> **ask ADO whether the create already
landed** (`FindByIdempotencyTagAsync`, a WIQL tag query) -> create only if it did not -> record
the outcome. All of it **outside** the step-10 transaction, deliberately: a record that rolled
back with the local half would be erased by exactly the crash it exists to survive.

So #270's failure mode is gone. A crash between 7 and 10 leaves an intent with no outcome; the
retry finds the orphan by its stamped tag and adopts it instead of creating a duplicate. The
ATTACH-window risk 0005 §4 accepted is covered by the same record -- no separate machinery.

A seed with no `StagedIdentity` predates 0014 and takes the old unprotected path rather than
being given a fresh identity that would match nothing already in ADO.

### Verified against live ADO -- and it caught a real bug

The recovery clause was smoke-tested against a real project (`dangreen-msft/Twig`) with a
throwaway work item, created and deleted. **The first version did not work.** ADO answered:

```
HTTP 400 -- You cannot supply a time with the date when running a query using date precision.
The error is caused by <<[System.CreatedDate] >= '2026-07-27T15:02:58Z'>>.
```

This is the worst possible failure shape: the recovery query returns nothing, the orchestrator
concludes the create never landed, and the retry duplicates the work item -- **precisely the #270
bug this ticket exists to close**, reintroduced by the fix for it. No mocked test could see it,
because the mock answers whatever it is told to.

The fix is `timePrecision=true` as a **query-string parameter** on the WIQL endpoint. Two dead
ends confirmed on the way: a body-level `timePrecision` field is silently ignored, and dropping
the time component degrades the fence to day granularity, which is not a fence at all for a tool
that publishes many items a day.

Confirmed with a positive and a negative control: a fence one second BEFORE the create returns
the item; a fence five minutes AFTER returns nothing -- and returns it as an empty 200, not an
error, so the exclusion is real rather than an incidental failure.

### Review found the guard was disarmed for the window it protects -- corrected

The first implementation shipped an **ordering bug**, caught in review on PR #309 and confirmed
independently at every cited line. Recorded here rather than quietly edited away, because the
failure mode is instructive: *the fix for #270 reintroduced #270*.

Both the intent close (`CompleteIntentAsync`) and the tag strip (`ClearIntentTagAsync`) happened
~30 lines **before** the step-10 transaction even began. So on the exact #270 rollback:

1. **No tag** -- stripped before the transaction, so `FindPublishedIntentAsync` could not narrow
   to the orphan.
2. **No usable ledger row** -- `RecordIntentAsync` preserved an existing intent only when
   `IsOpen: true`. A *completed* intent fell through to `ON CONFLICT DO UPDATE`, which reset
   `published_id` to NULL and re-stamped `recorded_at` -- destroying the proof the item existed
   **and** moving the fence past it.

`CreateAsync` fired again. Duplicate. The irony is exact: the comment above that line explains
why re-stamping `RecordedAt` reintroduces the duplicate, and the `IsOpen` condition then permitted
precisely that on the completed path.

**Why it survived the first round of testing: the ledger was WRITE-ONLY.** `GetIntentAsync` had
zero production callers (`git grep -- src/`), so no code path exercised recovery-from-durable-state
at all, and three tests were vacuous -- one asserted the in-memory object built *before* the
INSERT (deleting the INSERT left it green), and the recovery test never issued a second
`PublishAsync`, so "the retry does not duplicate" was never actually tested.

**The fix, three parts:**

- `ClearIntentTagAsync` moved to **after** the commit. The tag marks in-flight state, and the
  publish is in flight until the local half commits.
- `RecordIntentAsync` preserves **any** existing intent, open or completed. A completed row names
  the ADO id and is the only surviving proof the item exists.
- The orchestrator now **reads the ledger back** (`intent.PublishedId`) before asking ADO. That is
  the read path the ledger was built to serve and never had.

Every guard is demonstrated non-vacuous by reverting each half independently:

| Reverted | Tests that FAIL |
|---|---|
| tag stripped pre-commit | `RolledBackAttempt_DoesNotStripTheIntentTag`, `SuccessfulPublish_StripsTheIntentTagOnlyAfterTheCommit` |
| `IsOpen` fall-through | `RecordIntent_OnACOMPLETEDIntent_PreservesThePublishedId` |
| no ledger adoption | `SecondPublishAfterARolledBackFirst_AdoptsTheOrphanFromTheLedger`, `..._CreatesInAdoExactlyOnce` |

The recovery test now drives a **second** `PublishAsync` after a rolled-back first attempt and
asserts `CreateAsync` was called **exactly once** across both.

### Known limitation -- title collisions

The recovery predicate matches on title + type + creation fence. **If two seeds of the same type
share a title inside one publish window, the predicate is ambiguous.** An earlier draft of this
Answer described titles as rarely overlapping and attributed that to the owner; **there is no
provenance for such a confirmation, and it is withdrawn.** It is a real limitation of the
constant-tag design, mitigated but not eliminated by the `recorded_at` fence and by publishing
being serial and topologically ordered. A per-create key would remove it, at the cost of the
unbounded shared-tag growth this ticket rejected.

### Evidence

Suite green, all four projects exit 0: **7,398 passing** (Cli 2887 / Infra 1375 / Mcp 1313 /
Domain 1823), against a re-measured `e899de46` baseline of 7,379. (Counts shift against that
baseline because sibling PRs #306 and #307 landed underneath during the rebase.)

The regression tests fail on the unfixed code. Verified in a detached worktree at `e899de46`
with a probe using only symbols that existed then: the unfixed orchestrator calls `CreateAsync`
unconditionally even when the item already landed --
`NSubstitute.Exceptions.ReceivedCallsException : Expected to receive no calls matching`, exit 1.

### Follow-ons (IDs for the caller to assign)

- **PROPOSED: drain open intents at startup.** `GetOpenIntentsAsync` exists and is covered, but
  nothing sweeps it yet -- recovery today happens on the next publish of that same seed. A crash
  followed by the user never retrying that seed leaves the orphan unreconciled.
- **PROPOSED: honour `Retry-After` / HTTP 429 on the publish path.** 0001 §7 flagged documented
  throttling (`TF400733`) that the recovery path should handle; out of scope here.
