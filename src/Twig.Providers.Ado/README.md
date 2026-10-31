# Twig.Providers.Ado

Phase 3 of Epic #1220 adds an Azure DevOps `IProvider` for the accepted clean core.
This dual-target (`net10.0;net11.0`) AOT-compatible library references **only Core**.
Pass an explicitly opened clean-core `IStore` (normally `Twig.Store.Sqlite`); it
never discovers, initializes, migrates or resets a workspace database. The live
CLI and its native publisher/authentication composition are **not switched**.

## Binding and host integration

One provider instance binds one exact `Connection`. Resource scope is the native
collection `instanceId` GUID, destination is the immutable project GUID, and
`IdentityRef` is the host's stable authentication binding, not a credential.
Item IDs are collection-scoped positive canonical decimal strings. Revisions
are opaque, target-bound `ado-rev:<native revision>` observations. Never construct
or increment these in application code.

`AdoAccess.AcquireAsync` must acquire the existing host's operation/binding lease
and selected credential, refuse changed selection/actor/authority, and hold the
lease until `AdoSession.Dispose`. The session supplies the separate HTTPS
organization locator and the selected native principal ID. The adapter verifies
collection and authenticated principal through `connectionData`, resolves the
project **by GUID**, and verifies item project membership. It never searches for
another account or falls back to ambient credentials.

The mandatory `AdmitAsync` seam receives the exact immutable proposal, ordinal,
attempt, method, URI, media type and wire payload **after** the durable Applying
CAS and **before** dispatch. A host bridge must recheck native write authority and
durably admit those exact bytes under the original actor/binding. Its returned
`AdoWriteAdmission.ObserveAsync` must retain the native response for that admission.
Neither method may dispatch/retry the mutation itself; disposal must be nonthrowing.
There is no permissive token-only implementation. Tests implement only these
external auth/admission and HTTP boundaries; the adapter, validation, journal and
SQLite store are real. Phase 5 now supplies `CleanCoreAdoAccess` in Infrastructure. It requires the
existing native admitted binding/generation lease (not a token-only substitute),
rechecks the selected authority, binds immutable native actor identity and
requires the exact Core operation's durable Applying attempt before native
admission. Native intents/responses retain proposal digest, ordinal, attempt,
target and creation UUID correlation; acknowledged attempts and creation UUIDs
cannot acquire replacement requests. Absent correlation is omitted from legacy
JSON so historical immutable request bytes are not rewritten. This bridge has a
real native-auth/SQLite composition regression; retained command cutover is
still in progress and production registration is not yet switched.

The default HTTP transport disables redirects and retries. A supplied handler
is a trusted external transport seam and must preserve those properties. Response
bodies are bounded to 8 MiB; source-generated `TwigJsonContext` reads native JSON
and rejects duplicate members. Explicit UTF-8 writers emit wire payloads without
reflection or arbitrary object serialization. No telemetry or credentials are
written into receipts.

## Discovery, queries and reads

Discovery reads the project process identity, process-scoped types, per-type
fields, requiredness, states and rules, classic exact-reference transitions,
global field typing/read-only/picklist metadata, individual picklists, hidden
categories and native relation topology/direction. It never borrows a parent's
transitions or fabricates all-pairs transitions. Native protocol field references
(`System.State`, `System.WorkItemType`, etc.) are ADO adapter details, not a
process-template or custom-field vocabulary in Core.

Native `when`, `whenNot`, `whenWas`, `whenChanged`, `whenNotChanged`,
`makeRequired`, `makeReadOnly` and constant `copyValue` rules map to Core
predicates/actions (both sigiled and unsigiled spellings). Initial transitions
become an allowed-state rule conditioned on an absent pre-state; their values
participate in the schema digest. Picklists retain typed values and optional
clears. Disabled native rules retain their raw semantics in disabled fingerprinted
rules without granting authority. Unknown enabled rules/metadata, opaque field
kinds, unresolved default-value semantics, missing transitions or incomplete
discovery **block writes**, including non-field effects. Failed HTTP discovery
returns an error, never a successful empty/cached schema. This intentionally
conservative rule subset is not a claim to support every inherited ADO process
rule. Unsupported schemas require explicit additional translation, not dropped
rules or a bypass.

Explicit reads acquire at most 200 distinct keys. Full field observations
distinguish absent/unset (explicit Core null), empty text and typed native values;
undeclared native fields remain opaque rather than being coerced. Native identity
objects use their IDs, not display names. Forward/reverse relation endpoints
normalize to one discovered canonical edge with Provider provenance. Resource
links/attachments are not work-item relationships. Unknown/cross-collection
relations and contradictory acquired endpoints refuse completeness.

Queries preserve the exact ordinal, typed Core AST. WIQL acquires a bounded
project superset, then the adapter evaluates `All/Equal/And/Or/Not` locally:
native WIQL collation/null/NOT semantics are not equivalent to Core equality.
More than 200 candidates, continuation, unknown field/value shapes, unobserved
candidate fields or native pagination return explicit Unsupported/Unavailable.
There is no silent truncation, approximate predicate pushdown, calendar macro,
team iteration inference or unbounded board acquisition. Paths and iterations
can be discovered native fields and queried as such; they do not become Core ports.

## One-effect execution and recovery

1. Construct intent against acquired schema/items and the current store version.
   `ImportAsync(proposal, exactDigest)` reacquires authoritative inputs, runs Core
   validation, and atomically retains intent plus all Planned journal rows.
2. `ApplyAsync` requires that retained Planned operation, a fresh nonempty attempt
   UUID, unchanged schema and exact observed revision. It reads current evidence,
   refuses unresolved effects on either target (also across replacement digests),
   then wins the store CAS and persists Applying **before** host write admission
   or mutation transport. Provider reads never run inside SQLite transactions.
3. Supported native effects are Create, Batch, Relate, Unrelate and Comment.
   Batch/type change, relationship add/remove and history append use **one**
   JSON-Patch request with a numeric `/rev` test. State plus close gates therefore
   ride in the same atomic batch. No revision carry-forward/rebase/retry exists.
   Creation explicitly declares ObservedNonAtomic and retains its local UUID.
   Comment appends exact text/HTML through revision-checked `System.History`;
   Markdown requires conversion **before** confirmation, never in the adapter.
4. A complete definitive native 400/401/403/404/405/409/412/415/422/429 rejection
   records Failed/no effect. After possible dispatch, 5xx, async responses,
   cancellation, network loss, malformed/misattributed acknowledgements and lost
   native response durability record Indeterminate, not safe-to-replay failure.
   Admission refusal before dispatch records Failed with explicit no-dispatch
   evidence. Receipts retain actor/binding, request hash and native response.
5. Acknowledgement, journal, immutable create mapping and available item mirror
   commit together. Relationship completeness is not inferred from a mutation
   acknowledgement. Failed post-dispatch receipt persistence returns an explicit
   Indeterminate/StorageUnavailable receipt and leaves the original durable fence.
6. `VerifyAsync` accepts only the exact durable Applied receipt. A separate native
   immutable-revision read checks the acknowledged fields/type/history/links;
   native links also acquire current evidence from both endpoints. Current mirrors,
   receipt and journal advance commit together. Mismatch is Partial; unavailable
   readback is Indeterminate. Neither erases the original Applied receipt/mapping.
   Local overlays/tombstones, drafts, Bench, retained intent and history survive.

`RecoverAsync` only continues readback for durable Applied acknowledgements, or
records an Applying/no-ack attempt as Indeterminate. Terminal receipts are returned
unchanged. No negative/title search, replacement digest, expired lease or missing
mapping is positive evidence of non-dispatch. Unknown creation identity and comment
attempts stay fenced. There is deliberately no settlement or blind replay API.

**Explicit limits:** Delete is absent from capabilities and refused: this adapter
does not have a qualified atomic revision-checked ADO deletion route. That is not
a decision to remove the existing CLI command. Local-origin relationship effects,
native cross-project relationship mutations, Markdown transport, unsupported
schema/rule shapes and oversize queries are also refused. No effect is implemented
as a success-shaped stub. Multi-operation proposals stop after any unresolved
effect, and each later operation must still meet its original reviewed revision.

## Verification

Use the system SDK from `global.json` and the existing serial bounded gates:

```powershell
python tools\run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools\run-bounded.py --timeout 60 -- python tools\verify-trx.py -- dotnet test tests\Twig.Providers.Ado.Tests\Twig.Providers.Ado.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~Twig.Providers.Ado.Tests" --logger "trx;LogFileName=ado.trx" --results-directory artifacts\test-logs\core-ado
python tools\run-bounded.py --timeout 30 -- "C:\Program Files\Git\bin\bash.exe" tools/run-tests.sh --pre-push
```

The same focused adapter regression runs in Linux and Windows CI. Tests use
synthetic HTTP/auth boundaries and isolated fresh SQLite files, never real
credentials, user tracker drafts/journals, legacy databases or live publication.
