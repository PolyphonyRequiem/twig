# Change plans

Plan is the set of intended work-item changes. Local edits and seeds hold
editable intent (including original observations and local identities); they
are not a Plan. An editor can turn selected intent into typed Plan operations.
A proposal is a presentation of a Plan, not a separate domain object.
The **Core operation journal**, not a plan file or UI acknowledgement, records
execution outcomes.

Use the current canonical Core plan format. Older ADO plan v1/provider-plan
v2 documents and their review envelopes are not translated or supported alternate
formats. The [clean-core contract](../architecture/clean-core-contract.md) is the
detailed format and execution reference.

## Validate, preview, apply, inspect

```sh
twig plan validate --file change.json -o json
twig plan preview --file change.json -o json
twig plan apply --file change.json --confirm <exact-preview-digest> -o json
twig plan status --file change.json -o json
```

Each command requires an explicit file inside the workspace's admitted file
boundary. `plan` is the only CLI group for these routes. Run
`twig plan validate --help` for an example of the current file format.

| Step | What it establishes |
| --- | --- |
| Validate | Canonical format and digest only; no claim that current process, policy, or execution evidence has been checked |
| Preview | Current connection/schema/store/policy evidence, retained operation intent, and application blockers; it can write the local plan journal but never publishes |
| Apply | Exact digest confirmation followed by fresh admission, durable attempts, provider dispatch and verification |
| Status | Original digest's operation statuses, attempts and receipts without applying, settling, or replaying |

Editing canonical content changes the digest and requires a new review. A
successful preview is not a lease on provider revisions or policy. Apply rechecks
those preconditions. Do not flush or discard unrelated work to make an operation
applicable.

Publishing does not require one-time historical inventory certification.
Apply still checks the exact registered source, retained capture integrity,
target ownership, and unresolved prior attempts before issuing a write.

## Atomicity and provider differences

Multi-operation application is **not one distributed transaction**. Earlier
verified effects can remain committed if a later effect fails.
An ADO close transition and all coupled required gate fields must be in the same
atomic operation. Discover the connected type's requirements; do not stage the
gate fields before or after a separate state write.

ADO creation explicitly uses `ObservedNonAtomic`. Exact digest confirmation
binds the area and iteration IDs Twig reviewed, but does not lock the provider's
path-to-ID mapping. Twig refuses differences it detects before dispatch. If ADO
creates the item with different IDs, immutable revision readback leaves the
effect `Partial` and the creation UUID fenced against retry.

GitHub effects explicitly use observed-non-atomic concurrency. An opaque provider
observation is not remote compare-and-swap, and configured Twig policy is not
discovery of repository automation. Coupled atomic close gates cannot be waived
with non-atomic consent. See the [GitHub adapter contract](../../src/Twig.Providers.GitHub/README.md).

## Inspect an uncertain result

Status distinguishes Planned, Applying, Applied, Verified, Failed, Partial and
Indeterminate outcomes. A provider acknowledgement is not independent verified
readback. `allVerified: false`, a missing journal, or a failed process must not be
reported as completed work.

Preserve the original file, digest, attempt and journal. Inspect them before any
retry. Never redispatch an unknown create/comment, rebase an old observation
silently, or replay a verified prefix. `connection writes` exposes correlated
native transport evidence only; it has no settlement or replay authority.
Refresh-read affected items after journal inspection when appropriate.

## Human review surfaces

The CLI supports ordinary human/structured preview. Its current review model
reports operation identity/kind and available policy decisions; it is not the
former complete before/after presentation envelope. Inspect the plan's full
typed intent as well as `canApply` and blockers. The retired
`--interactive`, `--include-rendering`, draft envelopes and old OMP presentation
recipes are not current CLI routes.

The server's native Bench Plan session keeps the selection, scope and review
state outside Core. It assembles the same Core Plan operations per connection.
Save/Apply uses the exact retained capture and digest; agent reads, local
staging, and SDK view acknowledgements do not grant publication authority.
See [server security](../server.md#security-and-data-ownership).
