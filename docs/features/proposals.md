# Change proposals

A proposal is current **Core format version 3**, declarative intent identified by
the SHA-256 digest of its canonical bytes. The supported lifecycle is:

```sh
twig proposal validate --file .twig/proposals/change.json -o json
twig proposal preview --file .twig/proposals/change.json -o json
twig proposal apply --file .twig/proposals/change.json --confirm <exact-preview-digest>
twig proposal status --file .twig/proposals/change.json -o json
```

Only these four proposal verbs are current. `plan` aliases, `proposal seed`,
latest/reconcile helpers, v1/v2 envelopes and the old `PlanCommand`/lifecycle
service are not compatibility APIs. Historical documents must not be translated
silently into new digests or new attempts.

## Current document

The canonical writer is `Core.ProposalBytes` in `src/Twig.Core/Canonical.cs`;
the strict native file reader is `CoreProposalFiles` in Infrastructure. The
document contains `version`, `connection`, `schema`, `storeVersion`,
`policyIdentity`, `policyRevision` and an ordered nonempty `operations` array.
Files must remain inside the explicit workspace root and fit the native
8 MiB input bound. Unknown/missing/duplicate object members, invalid typed
tuples and unknown enum values refuse.

The connection contains exact provider, scope, destination and identity evidence.
Operations use provider-qualified remote keys or canonical local UUID keys,
typed field values, original revision evidence, effect kind and concurrency
mode. Optional relationship effects retain direction and explicit remove intent.
These are native Core values, not v1 integer aliases, v2 target envelopes or raw
REST JSON. Obtain identities, schema, policy and revision evidence from the
intended admitted workspace; do not invent them from display labels.

## Preview and publication

Validation checks the document and canonical digest without publishing.
Preview checks connection/schema/store/policy evidence and retains review state
through the native Core path. Its ordinary structured result includes the
digest, `canApply`, operation identity/kind and Core review data. `canApply`
describes current checks, not a remote lock or future permission guarantee.
Read the proposal itself for complete intended fields and consequences.

There is no captured presentation envelope, OMP presenter, interactive/full
review loop or rendering-width/color option. Render structured facts directly
for the destination without parsing terminal text or inventing a richer legacy
`reviewModel`.

Apply requires the exact canonical digest. Core rechecks native identity,
schema, policy, revision and journal evidence. ADO supports its qualified
revision-bound effects; GitHub's supported writes explicitly use
observed-non-atomic concurrency and do not claim server CAS or an edit lock.
Unsupported effects/providers refuse rather than choosing an alternate client.
Several operations are not a globally atomic transaction: a verified prefix can
remain after a later failure.

## Journal and recovery

Execution state belongs in the durable Core journal, not the proposal or a
renderer. Preserve original operation order, attempt UUIDs, receipts and
provider-qualified published mappings. Inspect status and refresh-read affected
items before reporting success.

An indeterminate write may already have succeeded. Never blindly replay it,
repeat a creation or reset local state to justify another dispatch.
`connection writes` exposes inspect-only correlated transport evidence; it does
not settle an attempt, replace the Core journal or clear a publication fence.
Report verified, failed, untouched and unresolved outcomes separately.

For a gated transition, discover required fields from the connected type and
carry the state plus all gate fields in one batch operation. No generic state
name, gate list or process template is assumed.
