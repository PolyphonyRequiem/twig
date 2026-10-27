# Change proposals

The change-proposal path is how an author's intent becomes a reviewed,
journal-tracked work-item mutation. A **proposal** is an immutable JSON declaration
identified by the SHA-256 digest of its canonical bytes. Version 1 retains the
existing ADO mutation lifecycle `validate → preview → apply --confirm <digest>`.
Version 2 adds provider-qualified identities and opaque provider revision
tokens, but is currently validation- and review-only; it cannot be applied.

This page is the concept map. For flags, exit tables, and per-command output
shapes see the [plans command group](../commands/plans/README.md).

## Why the workflow looks like this

Every version 1 apply eventually calls a PATCH against ADO. The proposal path
binds that mutation to the exact canonical proposal digest and records the
operation outcomes for recovery:

- The proposal file is **declarative and stateless.** All execution state
  lives in the **journal**, keyed on the digest.
- The digest is a **cryptographic identity for the intent.** It is stable
  across reformatting, and re-editing the file after preview invalidates the
  prior confirmation.
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

## Schema versions and identity binding

Version 1 is the existing proposal contract. Its integer `workItemId` and
`expectedRevision` fields, canonical bytes and digest contract are unchanged.

Version 2 replaces integer target aliases with an explicit binding between a
Twig-local identity and a provider-qualified identity. Its mutation target is
shaped like this:

```json
{
  "target": {
    "item": {
      "localId": "018f3f5a-6b7c-7d8e-9f01-234567890abc",
      "externalIdentity": {
        "providerKind": "provider-kind",
        "resourceScope": "stable-provider-resource-scope",
        "nativeId": "opaque-native-id"
      }
    },
    "expectedProviderRevision": "opaque-provider-token"
  }
}
```

These values illustrate the shape only; the provider adapter owns the
provider-specific scope and native-ID encodings.

`localId` must use the existing canonical `LocalWorkItemId` persisted spelling;
the external identity keeps provider kind, stable resource scope, and opaque
native ID separate. The provider token is preserved as an opaque string and
belongs only to the mutation target. A v2 link's `other` endpoint uses the same
local/provider identity pair but carries no mutation token. The supported v2
operation kinds are `batch`, `add-link`, `remove-link`, and `delete`;
`publish-seed` remains a v1-only operation.

These fields describe proposal identity and the expected token, not proof that a
provider offers atomic conditional mutation. The current lifecycle deliberately
does not import v2 into the journal or execute it: preview returns the complete
identity-aware review with `canApply=false` and a
`plan.unsupported_apply_version` blocker, and apply refuses immediately after
parsing a valid document, before checking the confirmation digest. That refusal
precedes journal access or mutation, pending-state reads, and provider calls.
Consequently, version 2 cannot inherit v1 integer identities or v1 execution
semantics by conversion, and no existing v1 document or digest is rewritten.

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
    twig proposal apply --confirm <digest>
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

For version 1, `twig proposal preview` recomputes the digest exactly as validate did, then
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

### Shared review, brief and expanded

Preview builds one canonical `reviewModel` for the built-in terminal presenter
and structured CLI/MCP consumers. It carries ordered operations, preconditions,
effects, blockers, choices and the exact digest. For version 1, cache-only
enrichment adds readable field labels, item links, immediate parent context and
staged seed display metadata; none changes the proposal identity or apply policy.
Version 2 review instead exposes its explicit provider identities and revision
precondition, without ADO-cache enrichment or journal import.

Human preview is brief by default: grouped item headings, scalar before-to-after
rows, and description replacement sizes instead of full bodies. Before values
are trusted only at the expected clean cache revision. Unknown observations,
known null, empty strings and explicit clearing remain distinct. Description
metrics strip equal prefixes/suffixes and count removed/inserted Unicode scalars
(`common-affix-replacement-v1`), including HTML source; this is not minimal edit
distance. A missing baseline yields no invented change count.

`--full` exposes available source values with terminal controls escaped. JSON
is always exact regardless of human density. Explicit `--interactive` adds a
TTY-only Details/Back/Cancel loop over the same captured observation; it never
refreshes or applies. Ordinary preview never waits for input.
See [preview flags and behavior](../commands/plans/proposal-preview.md) and the
[shared review contract](../specs/shared-proposal-review.md) for host obligations.

### 3. `apply --confirm <digest>` — the gated mutation

`twig proposal apply` requires **both** `--file` and `--confirm <digest>`,
and refuses to run without them
(`src/Twig/Commands/PlanCommand.cs:100-109`). The confirmed digest is
compared against the digest recomputed from the file's canonical JSON at
apply time. A change that alters canonical content — a value, operation, or
array order — fails the run; formatting-only whitespace and object-key-order
changes do not alter the digest. Re-preview after a canonical-content change
and pass the new digest.

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

### Per-operation diagnostics

Every journal row exposes a shared, bounded diagnostics projection through
`PlanJournalOperation.Diagnostics`
(`src/Twig.Domain/Services/Plan/PlanOperationDiagnostics.cs`). It is derived
per access from `State`, `ResultJson`, and (as a fallback for
`expectedRevision`) `RequestJson` — never cached, so a record-with-mutation
copy reprojects against the new payload. Both CLI and MCP surfaces render
this projection, so a row reads identically after apply and on a later status
call.

The projection is safe by construction:

- `disposition` is derived from journal `State` alone. Partial lifecycle
  states (`in-flight`, `awaiting-verification`, `not-started`) stay distinct
  from terminals — a row is never collapsed to a success it did not reach.
- `code`, `fields[].classification`, and `disposition` come from fixed
  vocabularies. A legacy row that predates the additive payload keeps its
  known disposition and leaves `code` null rather than fabricating detail.
- Field expected/actual bodies are deliberately never carried out of
  `ResultJson`. `ResultJson`, `Warning`, and `Error` remain the full-evidence
  route — human and minimal output print the bounded summary and a
  `full evidence: rerun with '-o json' …` hint, and JSON preserves the raw
  keys unchanged (AB#881).
- A stray `{"rev":N}` PATCH-acknowledgment shape is treated as an unknown
  payload. `rev` is not a proven post-op readback and is never surfaced as
  `ObservedRevision`.
- Terminal diagnostics enrich the acknowledgment in the same guarded journal update,
  including recovery from an already-`Applied` row. An acknowledged `rev:4` remains
  distinct from a later readback `revision:5`; a delete acknowledgment `deleted:42`
  survives even if its readback is unavailable. Unknown acknowledgment properties
  are retained. Readback-owned `revision`, `diagnostics`, `fieldEvidence`, and
  `failureDetail` describe the latest verification attempt. Opaque/non-object legacy
  results are retained verbatim under `acknowledgedResultJson`, without inventing
  an acknowledged revision. These evidence bodies remain excluded from compact output.

### HTML readback boundaries

For fields whose metadata declares HTML, supported serialization differences
verify with a normalization warning, using the same comparison in normal apply
and recovery. This includes trailing ASCII whitespace directly before a block
closing tag: `<p>Alpha</p>` and `<p>Alpha </p>` compare equivalent. ADO's
entity decoding (`&quot;` → `"`) and its insertion of ASCII whitespace-only text
between two supported block tags are also equivalent: `<p>A</p><p>B</p>` and
`<p>A</p>\n<p>B</p>` verify identically for `System.Description` and custom HTML
fields such as `Custom.FalsificationCriteria`. Whitespace beside inline or
preformatted elements, beside a block carrying `class` or `style`, nonbreaking
spaces, and encoded whitespace stay strict.

The whitespace fallback is deliberately narrower than a browser renderer. It
supports default-flow `div`, `p`, headings, block quotes, and lists, with balanced,
supported inline content. It does not trim inline text nodes, leading whitespace,
internal word separators, or nonbreaking spaces. Whitespace in `pre` and `textarea`
remains significant. Changed text, tags, attributes, and links remain contradictions.

Fragments carrying comments, `style` or `class`, unsupported elements (including tables and
foreign markup), and unsupported nesting retain the existing strict text comparison;
Twig does not infer their rendering or repair malformed HTML. For example,
`<strong>a</strong>b` and `<strong>a </strong>b` must not compare equivalent.
Even an inserted space before an inline closing tag at the end of a paragraph is
conservatively refused. These boundaries can still produce an `Indeterminate`
readback; they are not permission to ignore HTML fields or repeat a write blindly.
Revision, clear, scalar, and unavailable-readback checks are unchanged.

## The digest, in detail

The digest has two jobs:

1. **Content identity.** It is the SHA-256 of the exact bytes
   `preview` and `validate` produce for the same file. Nothing learned after
   parse — an ADO revision returned from a PATCH, an id allocated by publish,
   or a readback warning — enters the digest
   (`src/Twig.Domain/Services/ChangeProposals/ChangeProposal.cs:20-25`).
2. **Journal key.** Every journal row is keyed on the digest
   (`src/Twig.Domain/Services/Plan/PlanJournal.cs:12-14`), so a later
   reader can go from a proposal file to its execution record without an
   intermediate lookup.

At apply time the caller must supply the digest shown by preview. A canonical
content change makes the old digest mismatch and the apply is refused; preview
again to obtain the new digest. Formatting-only changes do not alter canonical
content. The browser review page's explicit human Accept/Apply action is also
bound to the digest from the retained review, and refuses if the proposal
changed. There is no separate authorizer identity, steering-mode check, or
persisted approval snapshot.

## The journal

The per-workspace journal is the source of truth for what a proposal did.
Each row (`src/Twig.Domain/Services/Plan/PlanJournal.cs:10-68`) carries:

- the digest (primary key) and the source path,
- the canonical JSON captured at import, so recovery does not need the
  original file on disk,
- the top-level lifecycle state, `PreviewedAt`, and the separate `LastPreviewedAt`
  selection timestamp, plus `ConfirmedAt` and `CompletedAt`;
- the per-operation rows in declaration order.

Legacy audit columns remain in the durable schema and historic values remain
readable. New applies do not write those columns or persist a review-model
snapshot; the current review model is transient preview/canvas data.

## `proposal latest`

`twig proposal latest` reads the current workspace journal and selects its most recently
previewed header that is not fully `Verified`. Re-previewing an existing digest updates
`LastPreviewedAt` without changing `PreviewedAt` or lifecycle state. Failed, Indeterminate,
and partial states remain eligible; equal times use ascending digest order. The command
checks only the selected file against its journal digest and errors on missing or changed
bytes rather than falling back to an older entry. It does not scan proposal files or call ADO.

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
  The digest will not match; preview again and confirm the new digest.
- **"Loop over the operations client-side and PATCH them one at a time."**
  The per-operation journal rows are what make partial failures
  survivable. Rebuilding that in an agent duplicates code the domain
  already got right and — because the caller now owns retry — makes it
  possible to observe `Applied` without `Verified`, which the domain
  refuses to expose.
- **"Trust the top-level exit code."** Exit 0 means every operation
  reached `Verified`; exit 1 covers digest mismatch, pending rows, workspace
  drift, a foreign `Applying` lease, and
  any per-row failure. Automation that wants to react differently to any
  of these must read `PlanApplyResult.Error` and `Operations[i].Error`,
  not just the process exit.

The journal answers what the proposal attempted and which operations were
verified. Bypassing the digest gate or operation journal turns that result
back into a forensics exercise.

## See also

- [`proposal validate`](../commands/plans/proposal-validate.md) — parse, canonicalize, digest.
- [`proposal preview`](../commands/plans/proposal-preview.md) — import journal, snapshot pending, evaluate `canApply`, and optionally capture native version-1 presentation frames for a rich host.
- [`proposal latest`](../commands/plans/README.md#latest-unresolved-proposal) — select the latest unresolved journal header.
- [`proposal apply`](../commands/plans/proposal-apply.md) — digest-gated apply.
- [`proposal status`](../commands/plans/proposal-status.md) — read the journal row for a proposal file.
- [`proposal seed`](../commands/plans/proposal-seed.md) — describe a staged seed for proposal authoring.
- [`pending`](../commands/plans/pending.md) — dump of the same rows that block `canApply`.
- [Plans command group overview](../commands/plans/README.md) — canonical/deprecated verb map and journal notes.
