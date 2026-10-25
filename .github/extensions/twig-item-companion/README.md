# Twig item companion

This project-scoped Copilot canvas is the first read-only UI slice for Twig. It loads
one item through `twig bench detail <id> --document -o json` from the current
session workspace and displays the native version-2 detail document. It does not
read Twig's SQLite files or call an ADO/GitHub API directly.

The item companion intentionally does not edit, save, authorize, apply, post discussion,
upload/download attachments, or infer relationship coverage. The CLI remains the
authority for workspace admission and detail reads. Twig must be available as
`twig` on the extension process's `PATH`.

No dependencies are bundled. The Copilot runtime supplies
`@github/copilot-sdk/extension`. Local checks use Node's built-in test runner:

```powershell
node --test .github/extensions/twig-item-companion/tests/*.test.mjs
```

## Explicit workbench editing bridge (source only, not host-qualified)

The workbench source now imports `native-editing.mjs` and `editing-document.mjs`
through `editing-bridge.mjs`, with explicit browser OPEN/STAGE buttons in
`editing-client.mjs`. This integration delta passed ten module syntax checks,
59 focused cases and 126 cases across all fourteen offline extension test files
(zero skips/cancellations). The optional installed-native integration file was
not run. The panel-close regression failed against an HTTP-only-close mutant
and passed after exact source restoration. The initial focused run passed 57/59;
two deferred-detail fixtures lacked the qualified key/identity now needed by the
bridge and were corrected without weakening production validation or assertions.
The integration has not been activated in the installed host. A pushed native implementation does not
establish that the installed `twig` supports these commands. Unknown subcommands
or missing native envelopes surface unavailable/unknown outcomes, not readiness.

The bridge owns a cryptographically random capture handle and native context and
counters in the server. Browser STAGE requests carry only the handle and ordered
nullable field intent, never context/counter authority. Existing loopback
host/token/origin/content-type guards remain. Only `/api/editing` admits up to
256KiB JSON (the native serialized request still has its own 256KiB bound); other
routes retain 4KiB. Editing rejects duplicate keys, excess depth, unexpected keys
and malformed intent before invoking the adapter.

Only visible, non-contribution, non-read-only `FieldControl` values carried in
full or explicitly empty, with native `editable: true`, receive plain text/null
inputs. HTML, other control types and unsupported contributions remain explicitly
read-only. This is a transport capability limit, not inferred field rules.

Selection, Bench reads and identity-qualified target changes revoke the capture.
Unsent input remains in memory per checkout/connection/binding/item/Bench/identity,
never transfers to another target and is not automatically staged on a read.
A reopened different observed revision marks retained input stale and disables
staging until the human reviews/discards it and enters reconciled intent. Native
drift/conflicts also disable staging. Exact successful acknowledgement advances
the capture without clearing newer input typed while awaiting it; effect list
sorting or removal of baseline-equal edits is not treated as failed acknowledgement.
Refusal, transport unknown and indeterminate outcomes retain unsent input and
require explicit reopen. Disconnect/close aborts owned work; the busy latch stays
held until adapter closure, and unknown outcomes are retained in server state.
Panel close also awaits bridge idle after disconnect, rather than mistaking
closed HTTP connections for completed native cleanup.
No Save, apply, authorization or publication route is added.

OPEN is explicit and may acquire metadata and persist local evidence/draft
observations; it is not a read-only/cache-only detail fallback. STAGE sends one
bounded UTF-8 request through `draft stage --stdin -o json` and closes EOF. It
preserves the native context, ordered nullable intent and decimal-string Int64
tokens, without retry, rebase, Save, Apply or remote publication.

The adapter parses native structured stdout even on exit 1/2, preserving refusal
and committed/indeterminate evidence. Missing, malformed or contradictory output,
process failure and broken input transport produce an explicit unknown outcome,
never a rollback claim. Unknown-provider configuration failures may happen before
the command can emit a structured envelope. Existing inspection v1 and read-only
v2 contracts remain unchanged.

The adapter owns one invocation at a time. Its 15-second deadline and cancellation
request PID-scoped tree termination (Windows) or an isolated process-group stop
(POSIX); completion waits for the child close event and termination result, not
only the exec callback. Cleanup has a separate four-second bound. If closure or
cleanup cannot be proven, the outcome is unknown and that adapter instance stays
unusable for another child. Qualification outside the exercised Windows
cancellation/overflow cases is not claimed.

Output capture uses `spawn` and an owned **combined stdout plus stderr 4MiB**
limit. Exceeding that bound stops retaining output and requests tree termination
while the root is still alive; Node is not allowed to automatically kill only
the root on buffer overflow. Both streams remain drained until closure. If the
root nevertheless exits before PID-scoped termination can prove descendant
closure, the adapter reports unknown outcome and poisons reuse.

Native OPEN's normal-operation evidence/draft transaction is not a certified
cross-file WAL crash/power-loss guarantee. Postcommit origin fencing may return
indeterminate with committed true and an optional document. Callers must retain
unsent input and native intent, and explicitly reopen rather than retrying.

The contract tests use synthetic native envelopes, not native Twig execution.
The focused run passed 23/23, including real Node parent/inherited-descendant
cancellation and combined-output overflow through the actual supervisor. The
related offline consumers passed 83/83, with no skips/cancellations, and both
adapter modules passed syntax checks. The initial focused run failed one
acknowledgement-guard fixture because its drift flag was contradictory; that
fixture was corrected to reach the intended guard before the passing rerun.
Actual native OPEN/STAGE production DTO serialization (fake HTTP plus real SQLite
HostFact, not installed CLI/live ADO) has passed a separate adapter cross-check:
exact qualified fixture key/Bench/identity/context, request roundtrip, revisions
and generation acknowledgement, including null/empty/order preservation. This
does not qualify the new HTTP/UI bridge or installed host.

## Retention adapter and source-authored explicit bridge

The source-authored `createNativeEditingAdapter().retain({request, workspacePath,
signal})` invokes only `twig draft retain --stdin -o json`. The adapter is qualified
offline, not installed-host enabled. The explicit browser/server bridge described
below is qualified offline through the real rendered client and HTTP routes with
a fake native adapter, not live native RETAIN composition. No Save or
semantic-review action is wired.

Bridge qualification passed 49 focused cases and a deduplicated 120-case union:
119 passed, zero failures/cancellations and one existing opt-in native integration
skip. The exact named VM/HTTP regression failed on base `a40903dd` with only the
modified workbench test overlaid, at the runtime assertion that the explicit
Retain button must exist; the same one executed test passed on the candidate.
Name-filtered unselected tests are not coverage. These receipts do not qualify
live CLI/provider/installed-host behavior or claim later build/smoke gates passed.

The workbench's explicit **Retain staged draft** button sends only its opaque
server-held capture handle. The server derives native context and all counters
from the last admitted OPEN/STAGE document, refuses conflict/empty/equal-value
intent and consumes the handle on every native RETAIN outcome. It does not trust
browser `canRetain`, counters, effects, digest or authority. Unsent input disables
Retain: nothing automatically stages it. New edits made while retaining remain
unsent and are never replaced by a receipt.

Presentation is capture-only inert text: digest, operation ID, ordered native
effects and nonauthority/origin-coverage labels. Canonical proposal text and
native context are not sent to this UI. An absent baseline is labelled unknown,
separately from explicit null and empty text. No generic review model is inferred.

Known refusal permits explicit reopen, not automatic retry. Unknown transport or
committed-indeterminate RETAIN creates an inspect-first hold keyed by qualified
item, Bench and identity; OPEN cannot clear that hold or reenable RETAIN. The user
must inspect the local proposal journal before deciding any new capture; no
recovery authority or inspection callback is invented here. Server outcomes retain
committed/optional receipt evidence even after invalidation, disconnect or close
settlement, but old-target results do not populate a new target's UI. Client
holds/unsent input survive navigation in that client. These stores are
session-memory only, not durable recovery or reload-proof authorization.

Offline qualification passed 32 focused cases with zero skips/cancellations,
plus 74 related cases with one existing opt-in native integration skip and zero
failures/cancellations. A session-only crosscheck verified seven actual native
wire-file hashes and admitted both corresponding RETAIN request/receipt pairs
through validation and mocked transport; OPEN/STAGE belonged to a separate
fixture composition. This is not live native RETAIN composition, HTTP/provider,
installed-host, full-origin or authorization qualification. These receipts
precede integration; they do not assert that later integrated gates have passed.

The request contains version 1, exact native editing context and unchanged
decimal-string generation/baseline/observed revisions, with no effects, path,
digest or authorizer input. The adapter uses the same owned subprocess, stdin
EOF, deadline, combined 4 MiB output bound and unknown-outcome cleanup rules as
OPEN/STAGE. Exported pure receipt validation also bounds the serialized UTF-8
envelope to 4 MiB before canonical hashing or deep cloning, including aggregate
effects and canonical content. These are adapter resource bounds, not native
output limits or a maximum effect count. Accumulated drafts may contain more than
128 effective fields.

The retained receipt is distinct from an editing document: it preserves native
ordered effective effects, null versus empty, unchanged counters, exact context,
operation ID, digest and opaque canonical proposal string. SHA-256 checks the
exact UTF-8 canonical bytes without parsing or reserializing native numeric
revisions. This verifies byte integrity only: the adapter does not independently
prove that canonical operations and reported effects are semantically equivalent,
authenticate native origin, or produce a semantic review model. Native owns that
derivation. The operation-ID hash and proposal digest have different inputs.

`originCoverage` is only `editing-context-and-native-origin-digest`, not full
origin. `authorized:false` and `canApply:false` grant no authority and are not a
permanent journal-state guarantee. Committed-indeterminate results retain an
optional capture; missing/malformed output remains unknown, never rollback or an
automatic retry. Nothing exports a proposal file, authorizes, applies, publishes
or replaces unsent human input.

## Read-only v2 boundary

The adapter consumes the native export from `a5d6f5cf`, not a second item model
or parsed ANSI. It validates agreement between `itemKey.workItemId`, `item.id`,
`document.workItemId`, and the item/document revision before publishing an
observation. Missing identity, contradictory layout provenance, malformed form
structure or unsupported value states refuse the read rather than rendering
empty-looking success. Refresh requires the captured Bench, binding and identity
through CLI flags and checks the captured connection on the returned observation.
No active-item selection or `--sync` is issued.

The checkout/connection/binding/item qualification is the stable item key;
revision is a separate observed baseline. Refresh from r17 to r18 preserves
identity but grants no draft migration or edit authority. Server layout flags,
including `readOnly: false`, never grant editing: the UI explicitly displays
`editing: unavailable`. Layout status/source and field value states remain
distinct. Non-custom pages such as History carry structure only, not their
content. Missing appearance metadata remains unavailable; icon metadata grants
no asset-fetch authority. HTML and field values are rendered with `textContent`,
not interpreted.

Feature scope: this is a narrow item-reading foundation for #1176 and the shared
read boundary needed by #1175. It does not deliver #1175 Bench triage or shared
draft persistence, nor #1174 retained proposal review, assessment or digest-bound
authorization. No staging/edit/apply affordances are introduced.

`tests/fixtures/detail-v2.json` is fictional JSON matching the native v2 wire
shape (including omitted nulls and numeric field states). The integration harness
runs the production reader with that stdout at the process boundary, the real
loopback API, and the client rendering script in a minimal DOM harness. It checks
qualification, r17-to-r18 identity continuity, inert full values, layout/field
provenance and unavailable authority. These offline checks do not qualify
extension discovery, installation, reload, host placement or live authentication.

## Theme

The labelled Theme selector offers Auto, Light and Dark without changing host
attributes, Twig defaults or item state. Auto follows the documented host
`data-color-mode` on root/body; if neither declares Light/Dark, it follows the
system color-scheme preference and reacts to changes. Auto uses host semantic
tokens with appropriate light/dark fallbacks. Explicit modes use a local palette
even if the host changes mode. Keyboard focus covers the selector and disclosure
summaries as well as buttons/inputs.

The override belongs to the current document only and resets to Auto when the
iframe reloads. It is not advertised as a durable user preference: ephemeral
loopback origins cannot reliably provide cross-instance browser storage. No
localStorage, native preferences or new endpoint is used. This styling slice
does not add workbench/proposal-review surfaces or qualify actual host rendering.

## Named-Bench workbench (#1198)

The side-panel source now starts in the native Tree projection. Members remains
available separately, including known items omitted from the hierarchy. Branch
chevrons operate locally; filtering reveals matching ancestors without discarding
the previous expansion state. Source/configuration evidence is collapsed by
default, not removed. Selection can use separate tree/item pages with Back or
Escape, or a top/bottom arrangement with a labelled, keyboard-operable tree-height
range. Changing presentation never re-reads the Bench or changes shared state.
Back restores tree filtering, expansion, focus and scroll.

Native detail retains visible pages, section columns, groups and controls in
native order. Unsupported contributions and unexported page content are labelled;
field values remain inert text. The current explicit-Bench reader still accepts
only native fallback layout, not served ADO layout. No new editing, OPEN/STAGE or
Save contract is implied. Browser v2 does not export per-row provider/type
appearance, so this slice does not invent type icons or configurable provider
rails. These remain contract-backed follow-up work, along with full-view saved
queries, backlogs, Add to Bench and creation.

This navigation increment passed 21 focused workbench tests and 62 related offline
JS consumer tests (no skips/cancellations). All eight new regression cases fail on
the unchanged baseline with only the tests carried over. This qualifies the
offline consumer composition, not native-binary or host rendering. Historical
native receipts below do not qualify the new UI's host rendering.

The same provider also declares `twig-workbench`, with `load_bench({ bench })`
and `refresh_bench({})` actions. The separate workbench panel accepts an existing
saved name or decimal ID; its reader invokes only:

```text
twig workspace --bench <name-or-id> --include-browser --browser-version 2 --view tree -o json
```

This contract is pinned to native prerequisite **#1210**, commit
`3ff46fa7565b2231a872082cd34d0e1988b86a7e`. It is not available in older Twig
binaries. An unsupported invocation or schema is a visible refusal, never an
ANSI parser, current-Bench fallback, temporary switch or second query backend.
The UI branch consumes the exported contract; it does not carry that native
branch's ancestry. The native contract is now merged; older binaries still refuse.

Re-reading uses the captured stable Bench ID plus native expected Bench, binding
and identity flags, and checks the complete returned origin against the previous
observation, including connection/attachment evidence. Each CLI call has a
15-second timeout, abort signal, shell disabled and a 4 MiB output bound.
The server serializes reads; failure retains the previous qualified observation.
Its state is panel-local read evidence, not a durable draft or user preference.

Members retain native evaluator order, including known uncached IDs. The
hierarchy retains native nesting and ancestor-context labels; it does not parse
the native styled label. Filtering is local to loaded row text and retains
matching hierarchy ancestors. Counts distinguish loaded/known/uncached members
from rendered hierarchy nodes. Source totals and undiscovered counts remain
unknown, even when the cache is empty. Native omission IDs, projection limits
and reasons are shown; no continuation or complete-source claim is invented.
Saved area/sprint/pin configuration is inert read-only JSON.

All rows are native **raw-mirror** evidence, not composed durable-edit values.
Seeds are labelled local seeds. Pending markers are not edit permission or proof
that displayed values include local changes. Selection shows the qualified raw
summary and restores filter/view/focus on Back or Escape, without `twig set` or
changing shared current-Bench state. Untrusted text is inserted using
`textContent`, never HTML or arbitrary links/assets.

**Explicit-Bench detail (real-process consumer validated):** selecting a
loaded member calls `twig bench detail <id> --bench <captured Bench id> --document
-o json` with `--expect-bench/--expect-binding/--expect-identity` (native contract at
twig `06cb33b2`, subsequently delivered through #484). It is cache-only (never `--sync`), never touches the
shared current Bench, and the reply's `origin` and `benchId` must equal the origin
captured by the v2 browser read, or the detail is refused. Non-member and not-cached
items surface the native error code. The form is Twig's fallback layout
(`layoutStatus: unavailable`), shown as inert text with membership and completeness
reasons; `sourceComplete` is never true and no remote child count is claimed.

**Failure boundary:** when the detail call fails or the native command is absent, the UI
keeps the raw-summary disclosure. Real-process evidence at extension head `34add476`
passed the full opted-in Node suite (40 tests, zero skips) against binary SHA-256
`b189eea5252c19e0f821bafda53c0b708cad49b5b70b6ffe1a332f0cec4e897e`.
That binary was built from helper commit `7a79fe935b2bf6fbec5a832aaa6d541dec4071fd`;
its production source tree equals main `d35c5d9b` (the helper changes are test-only).
The subsequent reparent to `2a9a1056` preserved the entire tested repository tree.
This proves explicit member detail, whole-origin continuity, nonmember refusal and
preservation of current Bench `Other` in the isolated native fixture, not installed
extension discovery, live credentials, host rendering, or host authorization.
Work-item completion remains a separate coordinator decision.
No editor, Save/capture, proposal review/Apply, provider handoff, install/reload or
host-runtime qualification is included. `tests/bench-detail.test.mjs` uses synthetic
fixtures over a fake process boundary: composition coverage, not native integration.

`tests/fixtures/bench-v2.json` pins the native v2 DTO/projection shape at the
prerequisite SHA. The workbench harness runs the real rendered client script
against the real loopback server and production reader, with only the CLI
process/filesystem boundary faked. Fixture evidence is not real-binary or host
qualification.

## Retained proposal-review consumer

`retained-review.mjs` exports `retainProposalReview(rawJson, expected)` and
`mountRetainedReview(root, rawJson, expected)`. These pure functions do not acquire
or authenticate a capture, call Twig, or authorize/apply. The separately declared
`twig-proposal-review` canvas now wires native acquisition to this same renderer;
host discovery and placement remain unqualified.

The caller supplies one complete retained native `proposal preview -o json`
envelope and an explicit expectation:

```javascript
{
    captureId: "host-observation-id",
    expectedCaptureId: "host-observation-id",
    expectedModelVersion: 1,
    digest: "<exact reported 64-character SHA-256>",
    workspace: { organization: "<reported organization>", project: "<reported project>" },
    sourceKind: "host-supplied-native-preview",
}
```

The expectation is caller context, not a new native wire schema or authentication
credential. The native envelope has no capture-authentication field. Validation
checks the known `twig.change-proposal.review` version-1 shape and internal
workspace/digest/ordered-operation correspondence. It cannot prove that JSON came
from Twig or detect a consistently forged or consistently shortened envelope.
The view labels workspace/digest/source as **reported**, and capture authenticity
as unverified. Missing expected identity/version, malformed or structurally
truncated input, unsupported kinds and contradictory evidence show unavailable.
Provider-qualified targets/peers, complete provider affected-item coverage and
opaque provider revision preconditions are supported within review model version
1. Proposal document version 2 is not review model version 2: unknown review model
versions still refuse. Provider identity retains the exact local ID, provider,
resource scope and native ID; opaque identities/revisions are never coerced to
integer ADO IDs. Numeric and provider target/endpoint alternatives cannot be mixed.
Provider deletion must match the complete target identity. Missing provider before
observations remain unknown, without fabricated cache values or change metrics.

Details and Back only switch the same detached, frozen observation; neither
acquires another preview. Brief retains every operation, effect, precondition,
issue, blocker, policy decision and pending-change record. Long `System.Description` field-set bodies (case-insensitive native classification)
are explicitly deferred to Details, which includes the complete envelope and
exact retained values. Text, links and markup remain inert `textContent`.
Authorization choices, recipe context and declared capability advisories are
visible inert evidence in Brief and Details, never actionable controls or observed
availability. Provider-qualified identity does not establish full native origin
or capture authenticity; neither is added or inferred by this consumer.
`canApply` or a favorable policy assessment does not grant this consumer authority.
The complete envelope is limited to 4 Mi characters; oversized input refuses,
never partially renders.

Acquisition is absent from the pure consumer. In particular, native `proposal preview`
imports/updates the local proposal journal: it is not a read-only acquisition
operation even though it does not apply ADO changes. No automatic preview,
refresh/re-preview, ANSI parsing, Save, approval or apply path is provided.
The authored tests use fictional JSON and a minimal DOM; they are not native
capture verification or end-to-end installed-host evidence.

### Explicit native acquisition canvas (#1195, JavaScript-qualified)

Open `twig-proposal-review` to get an empty, inert panel. The `acquire_review`
SDK action and the labelled form both require `{file, digest, workspace:
{organization, project}}`. They accept no pasted JSON, authority or origin claims.
One explicit action invokes exactly:

```text
twig proposal preview --file <physical-contained-file> --expect-digest <exact-digest> -o json
```

**This acquisition writes locally:** native preview may import a journal row and
update last-preview ordering. It never authorizes or applies. Invalid or unsupported
output may be refused after those local effects; cancellation is not rollback.
There is no automatic retry, refresh, validation preflight or second preview.
Native expected-digest checking precedes journal import. The native command also
owns admission, physical containment, workspace validation and journal origin.

The adapter pins physical session checkout/file and the exact expected/reported
workspace/digest, with a 15-second child timeout, 4 MiB output bound, abort signal
and no shell. The preview envelope does **not** export full connection/binding/
identity/Bench origin, so the canvas claims no full-origin continuity or host
authentication. Provider-qualified effects use the same review model version 1;
unknown review-model versions remain unsupported and are never partially rendered.

The provider-review consumer extension is offline-qualified: focused tests passed
15/15 and the four-file related union passed 60/60, with zero skips or
cancellations. The initial related run was 59/60: its stale negative fixture
expected an empty provider affected-item list to refuse. That fixture now tests
malformed evidence, while the reader explicitly accepts a valid empty list.

Eight parser-admitted proposals passed through the production review builder and
serializer with a fake repository; the candidate accepted all eight serialized
models. The same acceptance harness against the baseline failed first on missing
Brief authorization-choice evidence; this is not proof of six provider refusals.
The temporary Infrastructure test-project build passed with six SourceLink
warnings, not a supported-solution build. These receipts are not full CLI, HTTP,
SQLite, installed-host or origin-authority qualification. The capture-only
`draft retain` contract is separate and supplies no semantic review or full
origin; this consumer does not acquire or interpret it.

The complete validated observation is detached and frozen. Details/Back, Show
retained observation, iframe reload and close perform no preview. The protected
loopback state restores the same observation while this provider instance lives;
provider restart or a fresh panel starts empty, not with invented durable recovery.
Retained file/revision/readiness freshness is unknown until another explicit
acquisition. A failed replacement clears old success. Close aborts the owned child
and suppresses late publication; no Apply, Save or authorization route exists.

Qualification on base `9bf9372e3f7acbd65798527dff4cb77408ee1ba4` used the exact
reviewed executable/test blobs: eight `node --check` children passed in one
60-second bounded command, then one 60-second bounded command ran exactly
`proposal-canvas.test.mjs` (12), `retained-review.test.mjs` (8),
`workbench.test.mjs` (13) and `draft-canvas-integration.test.mjs` (11).
All **44 executed, 44 passed, zero failed/cancelled/skipped/todo**; Node and wrapper
exits were 0, duration 1922.9733 ms. Raw receipts are retained as
`1195-source-syntax8.log` and `1195-source-focused44.log` in the implementation
session's artifacts. The initial seven-file syntax inventory was corrected to
eight before execution; it was not a failed invocation. This ledger-only update
does not change those executable/test blobs.

These are fake-process, real-loopback, SDK-mock and inert-DOM results, not actual
native preview or installed review-canvas proof. Separately, the coordinator
opened an existing companion shell successfully, but its actual item load failed
with `Argument '--document' is not recognized` from the installed CLI. That
host/CLI mismatch remains a real failed load, not a passing native integration or
an eligibility/authorization refusal. No retry, installation or reload was
performed. The new review canvas is not yet discovered in that host; no Save,
host authorization or full-native-origin acceptance is claimed.
