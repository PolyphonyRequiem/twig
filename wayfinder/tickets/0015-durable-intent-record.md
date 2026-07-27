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

### The idempotency key: an ADO **tag**. No process-template change required.

The three candidates, settled with the API in hand:

- **ADO-side dedupe -- does not exist.** `research-ado-batch-push.md` §5 and §Gaps#7: creates have
  no `clientRequestId`, no dedupe token, no conditional-create. Verified absent, not merely
  undocumented. An ambiguous timeout is indistinguishable from a failure, and *"twig must
  reconcile by query before retrying creates."*
- **A custom field -- would require changing the owner's ADO process template.** That is an
  organisational decision, not a code one, so it was not taken and is not recommended.
- **A tag -- chosen.** Tags are per-work-item *data*, not schema. Microsoft Learn ("Add work item
  tags") records `Create tag definition` as a default Contributor permission, and tags are
  WIQL-queryable via `Contains`. Stamping one changes nothing about the process template, so this
  was safe to implement without escalating.

Format `twig-intent:<GUIDv7>`, derived deterministically from the `StagedIdentity`. Shape
constrained by the docs: no leading `@` (ADO reads it as a query macro, which makes the tag
unqueryable -- and an unqueryable tag cannot answer the question it exists to answer), no `;` or
`,` (tag separators would split one tag into two), under ADO's 400-character cap. Asserted.

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

### Evidence

Suite green, all four projects exit 0: **7,394 passing** (Cli 2883 / Infra 1362 / Mcp 1313 /
Domain 1836), against a re-measured `e899de46` baseline of 7,379.

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
