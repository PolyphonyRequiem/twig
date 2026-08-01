---
id: 0021
title: MCP becomes explicit-context only
type: task
status: closed
blocked_by: [0020]
---

## Question

Not a question — a **decision already made** (Daniel, 2026-07-31), captured here with its scope
pinned so it can be executed in one session. Open because it is **decided but unbuilt**.

> "we might want to consider redoing the surface and dropping the set/edit/etc. where a 'context'
> is implied and make MCP explicit context only"

## The defect this closes

The active work item lives in the **shared SQLite context store** — one row, one key
(`active_work_item_id`, `SqliteContextStore.cs:12`). It is **not** per-connection and not
per-session. The CLI and the MCP read and write the same pointer.

Five MCP **mutations** fall back to it when `id` is omitted. Every one of them writes:

| Tool | Site |
|---|---|
| `twig_state` | `MutationTools.cs:33` |
| `twig_update` | `MutationTools.cs:160` |
| `twig_patch` | `MutationTools.cs:235` |
| `twig_note` | `MutationTools.cs:318` |
| `twig_discard` | `MutationTools.cs:411` |

**Failure mode:** the user runs `twig set 4102` in a shell; a model mid-task calls `twig_note`
with no id and comments on 4102 instead of the item it believed it was on. Neither side is warned.
This is a **silent cross-surface write to the wrong work item**, and no test catches it because
both surfaces behave exactly as specified.

Aggravating detail, verified: MCP writes **shell prompt state** from four sites —
`ContextTools.cs:91` and `MutationTools.cs:62, 201, 504`. A model call can change what the user's
terminal prompt displays.

🔴 **This defect was invisible to 0020's research.** Asking "which scenarios deserve a tool"
cannot surface "the tools we have share mutable state with another surface." Worth remembering
before commissioning that shape of research again: a worth-of-scenarios lens is blind to
correctness defects in the existing surface, and this one is larger than anything that lens found.

## The rule

**Every MCP tool takes its target explicitly. No tool infers a target from the shared pointer.**

## Scope

### Signature changes (5)
The five mutations above: `id` becomes **required**. Not a redesign — the parameter already exists
and is merely optional.

### Needs a decision, not a deletion (1)
`twig_sync` resolves the active item to decide what to pull (`MutationTools.cs:476`). It needs an
explicit target or a defined default. **Resolve this inside the session; do not leave it implied.**

### Deletions (3) — 41 → 38
Cut as **consequences of the rule**, not as a quantity trim. The distinction is load-bearing for
the record: these are cut because the rule makes them illegal or pointless, which survives review.
"We had too many" does not, and was rejected — see "Trimming, rejected" below.

1. **`twig_set` — ILLEGAL under the rule** (`ContextTools.cs:25`). Its entire job is writing the
   shared pointer. It also warms a working set around the target (parent chain, two levels of
   children, links) and rewrites prompt state (`:78-91`). **Delete, do not deprecate.**
2. **`twig_parent` — POINTLESS under the rule** (`NavigationTools.cs:171`).
3. **`twig_children` — POINTLESS under the rule** (`NavigationTools.cs:157`).

Both crumbs exist so a model with implied context could feel its way one hop at a time. With
explicit ids the model already holds the id, and `twig_tree` returns the hierarchy in one call.
Cutting them **reduces round-trips** — the same N+1 argument that justifies `twig_batch`, so this
is consistent with 0020's evidence rather than in tension with it.

### Open question for Daniel — answer before or during the session
**May the MCP still *read* the active pointer?** `twig_workspace` reads it as one field of a
dashboard (`ReadTools.cs:56`). The memo argues it should be allowed: reporting *"here is what the
workspace currently points at"* is honest read-only observation, not an implied write target. If
"explicit context only" is **absolute**, this is a ninth affected tool and the response loses a
field. **Not decided.**

### Explicitly NOT in scope
The four remaining no-CLI-counterpart tools — `twig_cache_status`, `twig_tracking_status`,
`twig_list_workspaces`, `twig_verify_descendants` — all take explicit arguments or none. The rule
does not touch them and deleting them would be genuine capability loss
(`twig_verify_descendants` has real logic behind it in `DescendantVerificationService`).

Reads with an optional id (`twig_show`, `twig_tree`, `twig_refresh`) are lower risk — a wrong read
is visible to the model. Make them explicit for consistency, but they are not the hazard and
should not expand this ticket.

## 🔴 The caution that could reverse the `twig_set` deletion

`twig_set` is in the **advertised default eleven** (`McpToolCatalog.CompactToolNames`), so it is
likely something Daniel or Copilot reaches for routinely. Deleting it means a model can no longer
say "let's work on 4102" and have that stick across calls — every subsequent call carries the id.

**That is the intended behaviour change, and it is the one that will be felt.** If the stickiness
turns out to be load-bearing in daily use, that is a legitimate **bar-3 carveout** and it reverses
this specific deletion. Neither this ticket nor the memo claims to know; only Daniel's usage can
say. Do not treat the deletion as settled if he reports friction.

## Trimming, rejected — recorded so it is not re-proposed

Cutting the 30 hidden tools to reach the advertised 11 was considered and **rejected**. The
industry evidence (Sentry ~48 catalog / 9 advertised; Anthropic's 30+ → "search + execute") is
about **model selection accuracy**, which is driven by what the model is *shown*. Twig already
advertises 11 via `CompactToolNames`, so **that benefit is already collected**. Deleting the
hidden 30 buys maintenance reduction only — and would delete `twig_batch` (one of only two tools
that clear a bar on evidence, and currently **not** advertised), the entire nine-tool seed-publish
family, and four of the six no-CLI-counterpart tools.

**Separately worth doing under any option:** `twig_batch` is the strongest tool in the surface on
evidence and is not in `CompactToolNames`. Promoting it is a one-line change.

## Relationship to the freeze

**Not tool growth; does not conflict with 0012.** The count goes down. But it **is a breaking
change** to the MCP surface: prompts and habits relying on "operate on whatever is active" will
start erroring. That is the point — they should error rather than guess.

## Acceptance

- Five mutations require `id`; no mutation path reads `IContextStore` for a target.
- `twig_sync`'s target rule is decided and implemented, not left implied.
- `twig_set`, `twig_parent`, `twig_children` deleted from `McpToolCatalog` and from `Tools/`;
  `CompactToolNames` updated (`twig_set` is currently in it).
- Registration-completeness guards (0008) updated deliberately, **not relaxed**, per the pattern
  0014 and 0016 both followed.
- The workspace-read question is answered in this ticket before it closes.
- A regression test **fails on unfixed code**: assert that a mutation with no `id` is rejected
  rather than silently resolving through the shared pointer. Per the repo's testing convention,
  verify it fails at the pre-fix SHA in a detached worktree.

## Answer

**Resolved 2026-08-01. Implemented; the rule holds with one bounded, deliberate exception.**

### The rule as built

Every MCP mutation now takes a required, non-nullable `id`. Target resolution goes through a new
`WorkItemResolver.ResolveExplicitAsync`, which never touches `IContextStore`. The old
optional-id helper survives for **read-only** tools only, where a wrong target is visible in the
response rather than written to ADO. Both helpers carry doc comments stating which is which and
why, so the distinction is not folklore.

Because `id` is now a required value type, **the compiler enforces this** — an omitted target is a
build error, not a runtime fallback. That is what makes the fix structural rather than defensive.

### 3d answered (Daniel, 2026-08-01): the MCP may still READ the active pointer

`twig_workspace` keeps reporting the active item as one field of its dashboard, and the response
envelope keeps reporting it as metadata. **The rule constrains target resolution, not observation.**
Reporting *"here is what this workspace currently points at"* is honest read-only reporting; acting
on an unspoken pointer is the defect. No ninth tool was touched.

This distinction turned out to be load-bearing during implementation — see "corrections" below.

### `twig_sync`'s target rule: tracked trees, not the active item

Phase 2 previously resolved the active item and pulled its parent chain and children — making the
refresh set depend on the same mutable shared row, so what got refreshed could change underneath a
model with nothing named.

It now refreshes the **explicitly tracked trees** via `RefreshOrchestrator.SyncTrackedTreesAsync`.

Chosen over the alternatives (a required id; refresh nothing) because it is **not an invention**:
it is precisely what `twig sync` on the CLI has always pulled (`RefreshCommand` →
`SyncTrackedTreesAsync`). Tracking is explicit, durable, and user-declared through
`twig_track`/`twig_untrack`, so the scope is stated rather than inferred — and the two surfaces now
agree on what "sync" means, where before they quietly differed. Callers wanting one item still have
`twig_refresh` with an explicit id.

### Deletions (41 → 38), as consequences of the rule

`twig_set` (whole `ContextTools.cs` deleted — it had exactly one tool), `twig_parent`,
`twig_children`. Removed from `AllToolNames`, `CompactToolNames` (advertised surface 11 → 10), the
batch dispatcher, and DI/tool registration.

The 0008 registration guards fired exactly as predicted and were updated **deliberately**: the
counts they assert (41→38 full, 11→10 compact, 40→37 workspace params) were re-derived and
re-stated with a comment naming this ticket, not relaxed.

### Regression test — red-green verified

`ExplicitContextMutationTests` (9 tests). Three layers: `id` is structurally required on all five
mutations; a note with an explicit id lands on that id while the shared pointer names a different
item; and an IL walk proving no mutation can reach the active-context resolution helpers by any
path.

Verified against a detached worktree at the pre-fix SHA (`f7000b3d`): **7 of 9 fail there, 9 of 9
pass on the fix.** The two that pass at baseline are the two asserting the *permitted* read stays
permitted — correct, since that behaviour is unchanged.

### Two corrections to this ticket's own reasoning, made during implementation

Recorded per the repo's convention that a wrong premise gets corrected in the record, not dropped.

1. **"No mutation path reads `IContextStore`" was too strong as written**, and the first draft of
   the regression test asserted it literally — and failed against *correct* code. Every tool funnels
   through `EnvelopeBuilder`, which reads the active id to report it in the response envelope. The
   accurate rule is **no mutation path RESOLVES A TARGET from shared context**; the acceptance
   criterion above should be read that way. The test now targets `ActiveItemResolver` and the
   implicit resolver specifically, with a comment explaining why the blanket ban is wrong.

2. **The first version of the IL-walk guard was silently inert.** It passed at the pre-fix SHA,
   which should have been impossible. Cause: every method under test is `async`, so its IL body is
   a stub that starts a compiler-generated state machine — the real code lives in the state
   machine's `MoveNext`, which the walk never entered. It found nothing anywhere and reported
   success. Now fixed to follow `AsyncStateMachineAttribute`, after which it correctly fails at
   baseline. **A structural guard that cannot fail is worse than no guard**, and only the
   pre-fix-SHA check exposed it.

3. **This ticket INTRODUCED a defect, and the suite caught it: negative ids were rejected as
   invalid.** `WorkItemResolver.ResolveExplicitAsync` — new in this ticket — opened with
   `if (id <= 0) return InvalidInput`, added reflexively on the assumption that "ids are
   positive". They are not. A **negative id is twig's display alias for a staged, unpublished
   seed** (0003/0014's identity model), which is the same convention `McpToolCatalog` already
   encodes as `maximum: -1` on the four seed-only tools. The guard therefore broke **every seed
   mutation across the MCP surface** — a model editing a seed would have received a validation
   error instead of an edit. This was a live defect in the shipped path, not a test artifact.

   Fixed by rejecting only `0`, which is neither a published ADO id nor a seed alias.
   `WorkItemFetcher.FetchWithFallbackAsync` imposes no sign constraint of its own, so negatives
   flow through to the cache lookup exactly as the CLI's seed commands expect. Regression
   coverage is in `MutationToolsDirectIdTests` (`Mutation_WithNegativeSeedId_...`,
   `Mutation_WithZeroId_...`) and is red-green verified: both negative cases fail against the
   `id <= 0` guard and pass against `id == 0`.

   The general lesson, worth more than the fix: **an input guard written from a general
   intuition rather than from the system's own identity model is a behaviour change in
   disguise.** The suite caught this only because seed coverage happened to exist — luck, now
   converted into an explicit test.

4. **A second, quieter finding surfaced while rewriting the `twig_sync` tests: the MCP test
   fixture never registered `ITrackingService`.** `RefreshOrchestrator` takes it as an
   *optional* dependency and returns `0` when it is absent, so the tracked-tree refresh was a
   silent no-op under test and any assertion about it would have passed vacuously. The fixture
   composes the real `AddConnectionDomainServices`, but `ITrackingService` is registered by
   `AddConnectionServices` instead, so it fell through the gap. Registered explicitly in
   `TestConnectionScope` with a comment; the rewritten sync tests would otherwise have been
   green while proving nothing.

### Flagged, not fixed (as directed)

`SeedTools.cs:228` reads the active id to re-point context after a publish. Adjacent to this rule
but it is publish bookkeeping rather than target inference, so it is left alone.

`twig_batch` remains the strongest tool on 0020's evidence and is still not advertised. Promoting
it stays a live one-line follow-up, deliberately not bundled here.

### The behaviour change to expect

A model can no longer say "let's work on 4102" and have that stick — every later call must carry
the id. That is intended. Per the caution above, if it proves load-bearing in daily use, that
reverses **this specific deletion** and not the rule.

