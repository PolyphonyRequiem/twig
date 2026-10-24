# `twig draft show`

Inspect one **existing** recoverable native draft without changing intent.
Inspection may compare-and-swap local observation/conflict metadata; it is not a
strict filesystem-read-only command. It never creates a missing draft, flushes
pending work, creates a mutation capture or proposal journal, authorizes Save, or
publishes to ADO.

```text
twig draft show 42
twig draft show 42 --document -o json
twig draft show 42 -o minimal
```

The required positional ID is a native integer work-item ID or negative seed
alias, not a provider-neutral local ID. Output is human by default; JSON and
minimal are supported. `--document` defaults to false and requires JSON output.
Single-item inspection does not support `ids`. No refresh or sync flag exists:
item values are cached. Native form metadata may be read to acquire the semantic
document and separately acquired advisory edit capability. No MCP surface is
added while MCP delivery is withdrawn.

## Admission and identity

The command reuses native Browser read/edit observation and operation admission.
It correlates the semantic `CanvasItemKey` to the shared draft key without
allowing a caller to supply binding authority. Changed native origin, identity,
Bench, storage generation, item revision, local baseline, or draft generation
refuses; there is no automatic retry or silent rebase.

The native ADO baseline, observed, and current item revisions remain distinct
from draft generation, binding/selection revisions, durable local-record CAS,
and opaque provider revision tokens. A provider snapshot reader alone cannot
manufacture this draft envelope. The qualified key includes canonical checkout
root, connection ID, binding ID, and work-item ID, never panel or revision.

## Semantic document version 1

`--document -o json` emits one structured `draftDocument` render record with:

| Field | Contract |
|---|---|
| `draftDocumentVersion` | Integer `1`; independent of semantic detail version 2. |
| `itemKey` | `{checkoutRoot, connectionId, bindingId, workItemId}` from native admission. |
| `baselineRevision`, `observedRevision`, `currentRevision` | Original draft baseline, last recorded observation, and this cached native observation. |
| `generation` | Monotonic draft CAS generation, not a revision or authorization. |
| `draftStatus` | `empty` or `staged`, independent of reconciliation. |
| `status`, `reconciliationState` | `current`, `conflicted`, `blocked-local-work`, `field-unavailable`, `capability-refused`, `rules-refused`, or `rules-unknown`. |
| `freshness` | `cached`; never a claim of live server freshness. |
| `hasConflicts`, `localPendingWork` | Explicit booleans. Revision drift alone is a conflict. |
| `observationCompleteness` | `complete-for-draft-effects`, `incomplete-for-draft-effects`, or `blocked-by-local-work`; not whole-item completeness. |
| `changes` | Records with `fieldRef`, `baselineValue`, `proposedValue`. |
| `conflicts` | Records with `fieldRef`, `baselineValue`, `observedValue`, `proposedValue`. |
| `fields` | Per-effect observation and native advisory capability, described below. |
| `eligibility` | Advisory field/rule judgment; always `authorized:false`, `inspectionOnly:true`, `serverIsFinalAuthority:true`. |

Field values in changes/conflicts/observations are string or null, not arbitrary
provider scalars. Null and empty string remain distinct. Each `fields` entry has
`fieldRef`, `baselineValue`, `observedValue`, `observedValueAvailable`,
`proposedValue`, `nativePersistable`, `nativeCapabilitySource:"native-edit-sink"`,
nullable `eligibilityReason`, and `matchesBaseline`. Unavailable values are null
with availability false, never invented baselines or empty strings.

Eligibility has `fieldsPersistable`, `rules`, and nullable `rulesReason`.
Rules are `not-applicable` without a state effect, `unknown` without native
process metadata, or native `accepted`/`rejected`. Eligibility is not a promise
that the server will accept a transition.

## Local effects and refusals

Observation may advance generation and record revision drift or field conflicts
through the existing admission/operation gate and durable CAS. The original
baseline and proposed changes survive. Pending local work blocks value
observation and retains existing conflicts rather than treating local overlays as
remote facts. Unrelated pending rows, seeds, and proposal journals are untouched.

A missing draft returns exit 1 and a format-aware error, not an empty successful
document. Native admission and stale observation/CAS refusals return exit 1;
invalid ID/output combinations return exit 2. Errors are structured on stderr
for JSON, with no partial successful document on stdout. The command offers no
edit, capture, apply, or host-authenticated Save endpoint.

Without `--document`, JSON describes the retained local draft and minimal output
is one tab-separated row: item ID, generation, baseline revision, observed
revision, current revision, local/conflict/blocker status, effect count.
