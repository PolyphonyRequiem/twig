# Change proposals

The change-proposal path is how a mutation gets from an author's intent onto
someone's Azure DevOps board with a paper trail. A **proposal** is an
immutable JSON declaration of one or more work-item mutations, identified by
the SHA-256 digest of its canonical bytes, applied through the sequence
`validate → preview → apply --confirm <digest>`. The digest is the only thing
that carries authorization from a reviewer to the code that writes to ADO, so
every step of the workflow exists to keep that binding intact.

This page is the concept map. For flags, exit tables, and per-command output
shapes see the [plans command group](../commands/plans/README.md).

## Why the workflow looks like this

Every apply eventually calls a PATCH against ADO. Two things have to be true
before that happens: someone (or something) has to have _looked at exactly
this mutation_ and said yes, and the record of that sign-off has to survive
long enough for a later reader to reconstruct what happened. The proposal
path is built around both of those constraints:

- The proposal file is **declarative and stateless.** All execution state
  lives in the **journal**, keyed on the digest.
- The digest is a **cryptographic identity for the intent.** It is stable
  across reformatting, so "signing" a proposal cannot silently accept a
  reworded one, and re-editing the file after preview invalidates any
  authorization already collected.
- Apply is an **all-or-nothing exit** across per-operation rows. Partial
  success still returns exit 1; the journal preserves per-row outcomes so a
  caller can see exactly which operation stalled.

## Vocabulary: proposal, not plan

The canonical name is **proposal**. `twig plan …` is a retained deprecated
alias that dispatches through the same handler
(`src/Twig/Commands/PlanCommand.cs:12-19`) and produces the same output,
exit codes, and journal writes. New scripts, agents, and documentation
should use `twig proposal <verb>`. See the
[plans group README](../commands/plans/README.md#canonical-vs-legacy-verbs)
for the alias table.

Internally, the domain type is `ChangeProposal`
(`src/Twig.Domain/Services/ChangeProposals/ChangeProposal.cs:28-57`):
a validated `PlanDefinition`, its `CanonicalJson`, its `Digest`, an optional
recipe reference, and a free-text rationale. Every member is `init`-only on a
`sealed record`, so once rendered a proposal cannot be mutated at all.

## The lifecycle

```
        author edits proposal.json
                    │
                    ▼
        twig proposal validate     ── digest computed, no I/O
                    │
                    ▼
        twig proposal preview      ── journal row imported (Planned)
                    │                  pending changes snapshot
                    │                  canApply gate evaluated
                    ▼
    twig proposal apply --confirm <digest> --authorize <identity>
                    │
                    ▼
            per-operation loop
       (Planned → Applying → Applied → Verified)
                    │
                    ▼
       journal row settled (top-level state Applied / Failed)
```

### 1. `validate` — parse and digest

`twig proposal validate` reads the file, canonicalizes it (properties
sorted ordinal-ascending, array order preserved, whitespace collapsed), and
reports the lowercase-hex SHA-256 digest over those canonical bytes. It
never touches ADO and never writes to the journal. Two source files that
differ only in whitespace or property order reduce to the same
`CanonicalJson` and therefore the same digest
(`src/Twig.Domain/Services/ChangeProposals/ChangeProposal.cs:33-44`), which
is what lets a reviewer sign one form of the file and an agent apply
another.

### 2. `preview` — import the journal row and evaluate `canApply`

`twig proposal preview` recomputes the digest exactly as validate did, then
does three things a validate cannot: it **imports the journal row** keyed
on the digest, **snapshots every currently-staged pending change** in exact
staging order, and evaluates the **`canApply` gate**
(`src/Twig.Domain/Services/Plan/PlanPreviewResult.cs:11-49`).

`canApply` is true iff all of the following hold:

- the proposal is structurally valid,
- its workspace matches the active config,
- no pending row exists in the workspace's pending-change store,
- the journal row was imported successfully.

Any pending row makes `canApply` false. Proposals are **declarative-only**
and will not auto-flush pending edits — a caller who wants to mix an
in-flight `twig update` with a proposal must sync the pending edit through
its normal path first, or fold it into the proposal file. The rows that
show up in [`twig pending`](../commands/plans/pending.md) are the same rows
preview reports, in the same order.

### 3. `apply --confirm <digest>` — the gated mutation

`twig proposal apply` requires **both** `--file` and `--confirm <digest>`,
and refuses to run without them
(`src/Twig/Commands/PlanCommand.cs:100-109`). The confirmed digest is
compared **byte-for-byte** against the digest recomputed from the file at
apply time. Any mismatch — even a whitespace edit between preview and apply
— fails the run; the fix is to re-preview and pass the new digest.

Once the digest gate passes, the apply loop walks per-operation states:

- `Planned` → `Applying` → `Applied` → `Verified` for success,
- `Failed` or `Indeterminate` for terminal failures.

Every declared operation gets exactly one journal row keyed by digest and
ordinal (`src/Twig.Domain/Services/Plan/PlanJournalOperation.cs:8-52`).
Success payloads land on `ResultJson`; per-row failures land on `Error`; a
non-fatal server-generated rewrite (a timestamp ADO rewrote after the
PATCH, for example) lands on a separate `Warning` column so a Verified row
is never misread as failed. A top-level refusal — invalid file, digest
mismatch, pending rows present, workspace drift, or a live Applying lease
held by another actor — short-circuits the loop and lands on
`PlanApplyResult.Error`
(`src/Twig.Domain/Services/Plan/PlanApplyResult.cs:19-26`).

## The digest, in detail

The digest has three jobs, and picking any of them apart from the others
breaks the model:

1. **Content identity.** It is the SHA-256 of the exact bytes
   `preview` and `validate` will produce again on the same file. Nothing
   learned after parse — an ADO revision returned from a PATCH, an id
   allocated by publish, a readback warning — ever enters the digest
   (`src/Twig.Domain/Services/ChangeProposals/ChangeProposal.cs:20-25`).
2. **Authorization binding.** The digest a caller passes to
   `--confirm` is the digest that was authorized. `ProposalAuthorization`
   carries the digest independently
   (`src/Twig.Domain/Services/ChangeProposals/ProposalAuthorization.cs:40-65`)
   so a sign-off cannot be replayed against a proposal the authorizer
   never saw.
3. **Journal key.** Every journal row is keyed on the digest
   (`src/Twig.Domain/Services/Plan/PlanJournal.cs:12-14`), so a later
   reader can go from a proposal file to its execution record without any
   intermediate lookup.

Because the digest carries all three roles, **editing the file after
signing invalidates the signature by construction.** There is no way to
"forgive" a whitespace tweak between preview and apply: the recomputed
digest will differ, the digest gate will refuse, and the caller must
re-preview to obtain the new digest and re-collect an authorization.

## Authorization binding

`twig proposal apply --authorize <identity>` records _who_ authorized the
apply and — via the session steering seam — _in what mode_. The mode is
resolved from the session, never from a flag
(`src/Twig/Commands/PlanCommand.cs:111-124`):

- A **human-steered** session (`SessionSteeringMode` unresolved) requires
  a `ProposalAuthorizationMode.Human` authorization.
- An **AFK-steered** session requires a `ProposalAuthorizationMode.Model`
  authorization
  (`src/Twig.Domain/Services/ChangeProposals/ProposalAuthorizationGate.cs:44-47`).

The gate is a pure evaluation with no I/O and no permissive default
(`src/Twig.Domain/Services/ChangeProposals/ProposalAuthorizationGate.cs:15-47`):
a missing authorization, an authorization bound to a different digest, an
authorization in the wrong mode, and an authorization naming no authorizer
all fail closed. A missing `--authorize` is therefore not a usage error —
it returns exit 1, not 2 — because at the command layer it looks
identical to any other refusal
(`src/Twig/Commands/PlanCommand.cs:85-91`). The user did not mistype a
command; nobody signed the proposal off.

`Human` vs. `Model` is deliberately **not** the same vocabulary as the
session steering enum, and is deliberately **not** part of the digest.
Steering can change or be resolved late; "a human signed this" and "a
model signed this" are audit facts about one apply that must never move
afterwards (`src/Twig.Domain/Services/ChangeProposals/ProposalAuthorization.cs:3-13`).

## The journal

The per-workspace journal is the source of truth for what a proposal did.
Each row (`src/Twig.Domain/Services/Plan/PlanJournal.cs:10-68`) carries:

- the digest (primary key) and the source path,
- the canonical JSON captured at import, so recovery does not need the
  original file on disk,
- the top-level lifecycle state, plus `PreviewedAt`, `ConfirmedAt`, and
  `CompletedAt` timestamps,
- the authorization mode, authorizer identity, rationale, `AuthorizedAt`
  timestamp, and the review-model JSON the authorizer was shown,
- the per-operation rows in declaration order.

A `null` `AuthorizationMode` means _the row predates authorization
recording_, not _unauthorized_; rows written before the audit columns
existed are real history and are never rewritten. Consumers must treat
that null explicitly rather than falling back to "unauthorized"
(`src/Twig.Domain/Services/Plan/PlanJournal.cs:39-49`).

## `proposal status`

`twig proposal status --file <path>` reads the journal row keyed on the
file's current digest and reports its state. It never mutates the journal
and never calls ADO. The command has three distinct result shapes and the
exit code discriminates between them
(`src/Twig/Commands/PlanCommand.cs:145-166`):

|Shape|Meaning|Exit|
|---|---|---|
|Journal row loaded|`Found=true`, `State`, `Operations`, and (on prior failure) `Error` populated.|`0`|
|Valid digest, no journal row|File parses cleanly but has never been previewed.|`1`|
|Input error|Path outside workspace, unreadable file, invalid JSON, workspace mismatch — `Found=false` with `Issues`.|`2`|

If the digest has moved because the file was re-edited, status is looking
up the _new_ digest and will report "no journal" even when the old digest
has a full history. Re-run `proposal preview` to import the current row.

## Failure modes at a glance

|Where|What happens|
|---|---|
|`validate` sees a parser issue|Exit 1, digest still reported if the file at least parsed enough to canonicalize.|
|`preview` sees pending rows|`canApply=false`, but preview itself exits 0 — the pending rows are captured so a caller can decide what to do.|
|`apply` sees a digest mismatch|Top-level refusal, exit 1, no per-operation rows touched.|
|`apply` sees no authorization|Authorization gate refuses with the reason the session actually had; exit 1.|
|`apply` sees pending rows or workspace drift|Top-level refusal captured on `PlanApplyResult.Error`; exit 1.|
|`apply` finds a live `Applying` lease held by another actor|Short-circuit before touching the row; the other apply owns the run.|
|`apply` per-operation loop hits a PATCH failure|That row lands `Failed`/`Indeterminate` with the message on `Error`; other rows are attempted; exit 1 overall.|

`apply` exit 0 requires **every** operation to reach `Verified`. Anything
else — even one warning-only `Verified` sibling next to one `Indeterminate`
— returns exit 1. Callers must always inspect the per-operation rows on
`PlanApplyResult.Operations` rather than trusting the top-level result.

## Why an automation agent must not bypass the digest workflow

Every shortcut around this path breaks something a later reader will not be
able to reconstruct. The temptations, and what each one loses:

- **"Just PATCH the field directly."** No journal row, so `proposal
  status` reports no history and any later reader has to reconstruct the
  intent from the field change alone. `twig state` and `twig update` are
  fine for local staging that flows through `sync`; they are not the close
  path for a proposal.
- **"Re-use `--confirm` from an old preview after tweaking the file."**
  The digest will not match. Even if it did — because the tweak canceled
  out under canonicalization — the sign-off is bound to the pre-tweak
  bytes, and the audit trail would record a human authorizing something
  they never saw
  (`src/Twig.Domain/Services/ChangeProposals/ProposalAuthorization.cs:26-33`).
- **"Skip `--authorize` on AFK because nobody's watching."** The gate
  refuses; exit 1; the log entry names the session mode that required an
  authorization the caller did not supply. AFK still needs a `Model`
  authorization for exactly this reason: an AFK apply is a real mutation
  of someone's board, and the audit column has to name a party answerable
  for it.
- **"Loop over the operations client-side and PATCH them one at a time."**
  The per-operation journal rows are what make partial failures
  survivable. Rebuilding that in an agent duplicates code the domain
  already got right and — because the caller now owns retry — makes it
  possible to observe `Applied` without `Verified`, which the domain
  refuses to expose.
- **"Trust the top-level exit code."** Exit 0 means every operation
  reached `Verified`; exit 1 covers digest mismatch, authorization
  refusal, pending rows, workspace drift, a foreign `Applying` lease, and
  any per-row failure. Automation that wants to react differently to any
  of these must read `PlanApplyResult.Error` and `Operations[i].Error`,
  not just the process exit.

The whole point of the workflow is that the answer to "did this mutation
happen, who authorized it, and what did they authorize?" is a
one-database-lookup question. Bypassing the digest gate turns it back into
a forensics exercise.

## See also

- [`proposal validate`](../commands/plans/proposal-validate.md) — parse, canonicalize, digest.
- [`proposal preview`](../commands/plans/proposal-preview.md) — import journal, snapshot pending, evaluate `canApply`.
- [`proposal apply`](../commands/plans/proposal-apply.md) — digest-gated, authorization-bound apply.
- [`proposal status`](../commands/plans/proposal-status.md) — read the journal row for a proposal file.
- [`proposal seed`](../commands/plans/proposal-seed.md) — describe a staged seed for proposal authoring.
- [`pending`](../commands/plans/pending.md) — dump of the same rows that block `canApply`.
- [Plans command group overview](../commands/plans/README.md) — canonical/deprecated verb map and journal notes.
