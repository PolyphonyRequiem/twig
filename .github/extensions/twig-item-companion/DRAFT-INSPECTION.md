# Native draft inspection consumer - source only

This bounded foundation consumes the flat `draftDocumentVersion: 1` document
authored in native `e735705db8290944ca304e69ed9b56111e3f5283`.
That native change is source-only and is not assumed available in an installed
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
is not edited by this consumer. No SDK declaration or host action is registered;
there is no automatic invocation from existing companion or workbench panels.

Qualification correlates exactly checkout root, connection, binding and native
item ID. Separate detail and draft observations are independently captured.
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

The authored tests use fictional flat JSON, fake `execFile` and a minimal DOM.
They are not proof of native execution, merged delivery, host registration,
installation, reload, live credentials, or authenticated Save. Runtime and
JavaScript qualification await the coordinator's validation lease.
