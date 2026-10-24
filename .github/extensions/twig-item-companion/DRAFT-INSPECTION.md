# Native draft inspection - source-only explicit companion integration

This bounded foundation consumes the flat `draftDocumentVersion: 1` document
originally authored in native `e735705db8290944ca304e69ed9b56111e3f5283`,
with source-contract corrections at `043277`. Native G2 remains source-only and
unintegrated here; it is not assumed available in an installed
binary. This consumer does not modify the native implementation.

`createNativeDraftReader()` accepts an explicit native integer `workItemId`,
`workspacePath`, a captured semantic detail `expectedKey`, and optional abort
signal. It invokes only `twig draft show <id> --document -o json`, with the
existing 15-second timeout, shell disabled, 4 MiB output bound and canonical
checkout resolution. Native refusal messages remain errors; a missing draft
does not turn into an empty successful document. No set, init, sync, capture,
proposal, Save or remote mutation is issued.

Inspection is explicit, not an automatic refresh/read side effect. Native
inspection may CAS local observation/conflict metadata and read form metadata.
It is not zero-write and offers no offline guarantee. Baseline/proposed intent
is not edited by this consumer. The existing companion declaration now wires an
explicit `inspect_draft` action with an empty input schema (no caller-provided
key or item ID). It derives the exact key from retained, loaded native detail.
There is no automatic acquisition on open, detail load, refresh, state read,
render or polling, and no draft action on the named-Bench workbench.

The server serializes native operations. Attempting another detail/inspection
while inspection is pending cancels it and refuses the new operation until the
old operation settles; retry is explicit. Changing/re-reading detail, a refusal,
and close clear retained draft state. Cancellation plus a generation guard prevents
late completion from restoring invalidated draft evidence. Actual inspection
errors are retained separately and displayed, never converted to empty success.

Reopen the existing companion panel after the explicit action to display the
retained result or refusal through its token-protected state endpoint. Reopening
does not re-inspect. The iframe has no inspection POST endpoint or button.
It embeds the shared browser-safe validator and renderer as static source in
the existing nonce-bound script; no Node imports, eval, arbitrary asset serving
or CSP broadening is introduced. Physical cwd validation stays at the native
process boundary; ordinal identity and envelope checks are shared with the iframe.

Qualification correlates exactly checkout root, connection, binding and native
item ID using ordinal key strings. Physical checkout comparison is separate;
path-equivalent aliases cannot substitute a different qualified native key.
Field-reference correlation and duplicate checks are case-insensitive while
the original field strings and intent are retained verbatim.
Separate detail and draft observations are independently captured.
The draft document does not export complete Bench origin or expected-origin
flags, so this consumer makes no whole-Bench origin-continuity claim. Item-key
agreement is not host authentication, permission or cross-read atomicity.

`generation` remains draft CAS evidence, separate from native baseline/observed/
current item revisions. All four numeric fields must be nonnegative safe
JavaScript integers. Unsafe Int64 JSON numbers refuse, rather than rounding or
being used as an authorization/CAS token. No opaque provider revision is coerced.
Values are string/null, not arbitrary provider scalar values; null, empty string
and unavailable observation remain distinct. Per-effect coverage is mandatory.

`renderDraftInspection(root, document, expectedKey)` validates before rendering.
Invalid input clears the prior view and displays unavailable. Valid input is
inert text only: revisions/generation, qualified key, cached reconciliation,
draft-effects-only completeness, conflicts/pending work and advisory eligibility.
It has no buttons, editor, capture or Save affordances. Native flags must remain
`authorized:false`, `inspectionOnly:true`, `serverIsFinalAuthority:true`;
persistability or accepted rules never grants this surface authority.

Before this integration, both module syntax checks and all 10 focused tests passed with zero skips and
exit 0 at `28e3d2cde06852e8aa485c7c531bd47139ebe9f3`. The executable and
test file blobs were preserved exactly when restacked at `3018b7da`.
Those tests use fictional flat JSON, fake `execFile` and a minimal DOM; they
prove the JavaScript consumer boundary, not native execution or G2 integration.
The extraction and integration change executable source, so that historical
qualification does not qualify this candidate. New fake-process/server/DOM/SDK
tests are authored in `tests/draft-canvas-integration.test.mjs` but have NOT run.
No syntax, JavaScript, native or build commands were run for this source-only
packet. Native G2 integration remains withheld pending its own gates and lease.

Registration signatures are grounded in the installed SDK's `canvas.d.ts`,
`extension.d.ts` and `generated/rpc.d.ts` (runtime
`1.0.102-0.unstable.r40029025960.g5a14a15`). This extends the existing scaffold's
`createCanvas`/`joinSession` declaration, not a new host registration API.
Existing canvas capability discovery does not prove this new action is loaded.
Extension reload/inspect tooling, actual discovery of the new action, routed
action execution, iframe rendering and host lifecycle qualification remain
unproven; no install, reload or host UI action is authorized here.
Live credentials and authenticated Save remain unproven and out of scope.
