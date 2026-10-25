# Twig item companion

This project-scoped Copilot canvas is the first read-only UI slice for Twig. It loads
one item through `twig bench detail <id> --document -o json` from the current
session workspace and displays the native version-2 detail document. It does not
read Twig's SQLite files or call an ADO/GitHub API directly.

The canvas intentionally does not edit, save, authorize, apply, post discussion,
upload/download attachments, or infer relationship coverage. The CLI remains the
authority for workspace admission and detail reads. Twig must be available as
`twig` on the extension process's `PATH`.

No dependencies are bundled. The Copilot runtime supplies
`@github/copilot-sdk/extension`. Local checks use Node's built-in test runner:

```powershell
node --test .github/extensions/twig-item-companion/tests/*.test.mjs
```

## Unwired native editing adapter (source only)

`native-editing.mjs` and `editing-document.mjs` are isolated adapter source for the
separate frozen native editing v1 contract. They are not imported by the extension,
canvas server or rendered client. No new host action or HTTP endpoint is available.
Native qualification and explicit integration approval remain prerequisites.

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
No runtime success envelope from native Twig has yet been cross-checked.

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
Provider-qualified targets/peers, provider affected items and provider revision
preconditions are explicitly refused until their native consumer contract lands;
opaque identities are never coerced to integer ADO IDs.

Details and Back only switch the same detached, frozen observation; neither
acquires another preview. Brief retains every operation, effect, precondition,
issue, blocker, policy decision and pending-change record. Long `System.Description` field-set bodies (case-insensitive native classification)
are explicitly deferred to Details, which includes the complete envelope and
exact retained values. Text, links and markup remain inert `textContent`.
Authorization choices remain evidence in that envelope, never actionable controls.
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
authentication. Provider/model-v2 material remains explicitly unsupported: it is
not downgraded or partially rendered.

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
