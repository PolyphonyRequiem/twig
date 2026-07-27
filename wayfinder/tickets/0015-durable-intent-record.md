---
id: 0015
title: The durable intent record
type: task
status: open
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

<!-- empty until resolved -->
