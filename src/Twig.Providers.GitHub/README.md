# Twig.Providers.GitHub

Phase 4 of Epic #1220 adds a standalone `IProvider` over Core and an explicitly
opened `IStore`, normally the real SQLite adapter. The library references **only
Core**, targets `net10.0;net11.0`, enables AOT/trim analysis and uses source-generated
`TwigJsonContext` plus explicit UTF-8 writers. Production commands, credentials,
legacy storage and migrations are unchanged. This is not a live-command bridge.

## Authority and schema evidence

`Connection.ResourceScope` is the canonical HTTPS API origin. `Destination` is the
canonical JSON tuple `[immutableOwnerId, immutableRepositoryId]`, produced by
`GitHubProvider.Destination`. Native issue node IDs, not issue numbers, are keys.
Repository names/URLs are locators, not identity. Each leased `GitHubSession`
supplies the explicitly selected `Gh` or `GitHubApp` user credential, stable actor,
attachment/credential acquisition context and GraphQL endpoint. The adapter verifies
native viewer, repository and owner IDs and revalidates the host lease. It never
changes accounts, requests grants, follows redirects or retries mutations.

The mandatory host `AdmitAsync` runs **after** durable Applying and receives the
exact proposal, ordinal, attempt, method, endpoint and payload. The host must
durably admit those bytes under the original selected binding/actor and hold its
lease; `ObserveAsync` must durably retain the native response for that admission.
Neither seam dispatches/retries the mutation. Disposal must not throw. No
token-only or permissive default implementation exists.

Introspection discovers supported API field/input/mutation names and state values;
the repository issue-type catalog supplies stable type IDs. The untyped native
Issue shape remains explicit. OPEN/CLOSED are GitHub protocol semantics, not an
assumed project process. Unknown state semantics and incomplete catalogs refuse
writes. Native capabilities describe implemented schema shapes, **not** runtime
permissions or availability; denial remains an error.

**GitHub process/workflow rule discovery is Unsupported.** This remains
fingerprinted in disabled, typed schema evidence. The user-approved phase-5
validation scope is **native API constraints plus explicitly configured Twig
policies**, not external repository automation. An absent optional Twig
declaration means no declared local rules, never that external workflows have
been discovered, satisfied or waived.

`ReadRuleProjectionAsync` must acquire connection/actor/native-fingerprint-bound
`GitHubRuleProjection` evidence explicitly naming that scope. Policy identity,
actual revision and per-type field rules participate in confirmation. Missing,
stale, mismatched, unknown-scope or unsupported evidence creates schema gaps and
refuses effects. Unknown enabled rules still refuse. Native type names are
retained as fingerprinted evidence so configured policy names can resolve to
actual stable native type IDs without guessing.

The host `GitHubPolicyProjectionSource` reads the real optional `twig.policy.json`
once, hashes its exact bytes and translates only its supported mechanical
parent-type/dependency rules. Duplicate/unknown members, invented `complete`
flags, ambiguous/unknown types, unreadable declarations and excessive acquisition
sizes refuse. Its current declaration format has no field-rule/close-gate kind;
it does not manufacture one or infer business rules from model text. It returns
the captured mechanical policy for application enforcement alongside the bound
field-rule projection. A source acquisition alone is not enforcement or live
write authority.

**Live command writes are not switched.** Phase 5 must connect the real leased
admission bridge and captured-policy enforcement explicitly; it cannot invent
external rules or treat the test seam as a production authority.
Tests supply unmistakably synthetic, test-only policy evidence and HTTP/auth
boundaries; no live credentials or remote writes qualify this implementation.

## Reads, queries and relationships

Reads acquire at most 100 distinct native keys. Queries acquire the repository's
first 100 issue candidates and evaluate the exact typed Core AST locally. Any
additional candidate/type/label/assignee/relation page returns Unsupported before
claiming completeness or persisting that acquisition. There is deliberately no
approximate search pushdown or continuation API. Unknown field queries refuse.
Observed null, empty Markdown, empty identity sets and absent fields stay distinct.
The opaque observation token hashes native ID/type/timestamp, typed fields and
native edges; it is **not** a revision number or remote CAS. Scalar observations
are checked again after connection acquisition; contradictory acquired endpoints
refuse. These are multi-request observations, not a remote snapshot transaction.

Native parent/sub-issue and blocking/dependency relationships are read and written
only with their actual introspected schema shapes. Each mutation still checks its
specific native mutation's presence; permission failures never switch transport.
Only when the whole native read pair **and** its mutation family are absent is
that kind explicitly local. Partially present native shapes, including mutations
without a matching read pair, refuse rather than manufacture absence. Related is
explicitly local because this adapter has no native Related transport. Local
effects use digested Local provenance and exact edge/tombstone CAS; native effects
use Provider provenance. Reads persist refreshed mirrors through `IStore` while
retaining all local edges/tombstones, drafts, Bench definitions, mappings and journal.
Cross-repository native relationships/resources refuse. No network runs inside
SQLite transactions.

## One-effect execution and recovery

`ImportAsync` validates exact digest, current schema/store version and real acquired
inputs, retaining intent and all Planned rows together. Apply rechecks authority,
schema, exact opaque observation and unresolved-target fences; it wins store CAS
and persists a fresh Applying attempt **before** admission or native dispatch.
Every native effect explicitly declares `ObservedNonAtomic`. No exact-revision or
atomic-batch guarantees are claimed, even for one native request.

Supported effects are one-request creation, independent field replacement groups, Markdown
comment, and native/local relate/unrelate. Writable fields are title, Markdown body,
native state, milestone ID/null, sorted unique label IDs and assignee IDs where
their native input exists; resource identity/kind/repository is checked. Empty
Markdown explicitly clears a body; null body is refused, not silently coerced.
Creation binds its native OPEN initial state and optional discovered issue type in
one request. Comment readback uses its acknowledged native ID, subject and author.
Independent fields may share one `updateIssue` request only with digested
`ObservedNonAtomic` intent. This does not claim atomic application: separate
readback checks every field and records Partial when any differs, retaining the
acknowledgement and replay fence. Type changes and Delete refuse. Independent
multi-request effects must be exposed as separately ordered operations **before**
digest confirmation. A close gate must ride atomically with its transition;
non-atomic consent cannot bypass that invariant, so coupled closes refuse.
Original observations are never
carried forward or rebased after an earlier effect.

Complete HTTP rejection records Failed/no effect; GraphQL errors/partial data,
5xx, network/cancellation after dispatch, missing/misattributed acknowledgements
or lost native-response durability record Indeterminate. Applied requires the
request's exact echoed `clientMutationId`. Acknowledgement, journal and immutable
creation mapping commit atomically; no fields are fabricated from an ID-only
acknowledgement. Separate readback persists actual mirrors and a Verified, Partial
or Indeterminate receipt without erasing the original acknowledgement. Local edge
change and acknowledgement share one store transaction, then verify separately.
Post-dispatch persistence failure retains the original durable fence.

Recovery reads durable Applied acknowledgements under the original authority.
An Applying/no-ack attempt becomes Indeterminate without sending a mutation.
Creation UUIDs remain fenced across crashes, reopen and replacement digests.
There is no title-only adoption, negative-search settlement, create/comment replay
or automatic recovery from an external host's otherwise unimported response.

## Verification

Serial repository gates (system SDK, synthetic boundaries and fresh SQLite only):

```powershell
python tools\run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools\run-bounded.py --timeout 60 -- python tools\verify-trx.py -- dotnet test tests\Twig.Providers.GitHub.Tests\Twig.Providers.GitHub.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~Twig.Providers.GitHub.Tests" --logger "trx;LogFileName=github.trx" --results-directory artifacts\test-logs\core-github
python tools\run-bounded.py --timeout 30 -- "C:\Program Files\Git\bin\bash.exe" tools/run-tests.sh --pre-push
```

The focused HTTP/real-SQLite regression also runs on Linux and Windows CI.
