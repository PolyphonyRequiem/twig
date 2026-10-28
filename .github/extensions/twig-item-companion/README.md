# Twig item companion

This project-scoped Copilot extension provides cached item detail and a named-Bench
workbench. Detail and ordinary browsing remain read-only; the workbench also exposes
explicit local draft open/stage/retain and a separate human Save backed by Twig's native bridge. A separate
proposal-review canvas provides explicit acquisition and a human digest-bound Accept/Apply action. It does not
read Twig's SQLite files or call an ADO/GitHub API directly.

Ordinary item-detail reads remain read-only. Both the companion and workbench can explicitly open a local
draft and stage allowlisted field intent, but neither action saves, authorizes, applies,
publishes, posts discussion, uploads attachments, or infers relationship
coverage. OPEN may persist local observation metadata. The CLI remains the authority
for workspace admission, target qualification and local draft transactions. Twig must
be available as `twig` on the extension process's `PATH`; see *Executable admission*
below for how each canvas pins and checks it.

No dependencies are bundled. The Copilot runtime supplies
`@github/copilot-sdk/extension`. Local checks use Node's built-in test runner:

```powershell
node --test .github/extensions/twig-item-companion/tests/*.test.mjs
```

## Executable admission and capability preflight

Each opened canvas instance resolves `twig` once from absolute `PATH` entries
(`twig.exe` only on Windows), canonicalises it to its real path, and records the
file's SHA-256. Every native command in that instance then spawns that absolute
path, never a bare `twig` name. Reopening the same instance does not re-admit;
a new instance does.

Each canvas declares only the native capabilities it uses (item: detail, draft
show and draft open/stage/retain/save; workbench: Bench browse/detail, discussion
read and prepare, attachment reads, proposal preview/apply, and draft
open/stage/retain/save; review: proposal preview/apply). Before a capability's first use, the instance runs
`<command> --help` on the pinned path and requires that the usage line names that
command and that its `Options:` section declares each flag the canvas will pass as
an exact token. A generic or different help page, or a prefix of a required flag,
does not admit. There is no minimum-version check. A refusal is latched for the
instance and surfaces as `twig_capability_missing`; opening a canvas runs no help.

Before **every** spawn, including help probes, the file is re-hashed. Any change
since admission, including same-size rewrites with a restored modification time,
refuses with `executable_changed` and latches. This is a best-effort check made
immediately before spawning, not an OS-atomic guarantee: a file replaced between the
hash and the process start is not detected. Successful probes are cached per
real path and hash, never per `PATH` name. While searching `PATH`, only "not
usable here" lookup errors (for example missing, not a directory, or access
denied) skip to the next entry; any other I/O failure is reported with the
candidate path instead of falling through.

These typed admission errors are built locally from the pinned path, hash and
capability reason. The discussion panel relays them verbatim, but no other native
failure: provider and cursor diagnostics there remain redacted. They also disable
**Read discussion** for that instance and ask you to reopen the canvas, because
reading again with the same pinned executable cannot succeed. A previously
displayed page stays visible, labelled as a previous observation.

Help output grants no authority. Every native payload validator and guard runs
independently of preflight. Each probe is bounded to 5 s and 64 KiB, with 15 s
total probing per instance. The timeout bounds duration only: it stops the direct
child, not descendants it may start. Help is not claimed to be offline or free of
side effects; an older or unknown binary could perform startup work, including
network access, while printing help. Preflight installs, downloads, reloads and
changes nothing in the environment.

## Companion runtime context (source only)

The companion's **Check runtime context** button and `refresh_context` SDK action
explicitly compare the joined SDK runtime session and canonical local checkout
with the displayed native item key. Opening, loading and re-reading cached detail
do not discover an active item, send chat, claim work or start a run. A changed
runtime session, remote session or different checkout is a visible mismatch;
unavailable metadata remains unknown. Detail reads invalidate previous context,
and late metadata cannot relabel a different item. Context checks have a five-second
deadline; this bounds observation, not cancellation of the SDK metadata request.

Matching the checkout is **not** evidence that the conversation is linked to that
item. The inspected host contract provides neither authoritative linked-item/run
discovery nor app destination/focus navigation. Both remain visibly unavailable;
runtime session IDs are not converted into app navigation links. This is a partial
source implementation of #1205, not linked-run or installed-host qualification.

## Companion quick edits (source only)

After an explicit published-item detail selection, the companion mounts the same
`editing-client.mjs` and `editing-bridge.mjs` as the workbench, backed by the same
`native-editing.mjs` and native Save adapter. No second editing, capture,
authorization, journal or conflict implementation is introduced. Local seeds do
not acquire these editors. OPEN/STAGE/RETAIN remain explicit; only a separate
trusted human **Save exact retained capture (publish)** gesture submits its
server-held capture. No SDK Save action is exported. Native field eligibility,
single-use handles, uncertain-outcome holds and unsent-input isolation are shared
unchanged. Native surface attribution retains the shared workbench spelling; it
does not attest an authenticated person or an installed host.

Only a native `saved` result with verified effects triggers a qualified cache
detail re-read. Its original checkout/connection/binding/item/Bench/identity
must still match. Unverified or locally unsettled outcomes never refresh or
replay. A subsequent detail-read failure is reported separately from the already
verified Save; the old observation and native outcome remain available. Neither
reopening nor selection changes silently clear a native inspect-first hold.

Regression coverage runs the actual iframe, loopback server, shared bridge/client
and native JSON adapters with only the SDK/process boundary substituted. This
does not establish installed-host or live-tracker Save qualification.

## Explicit read-only workbench discussion

After qualified detail for a published cached member is loaded, **Read discussion
(online)** reads one remote provider page through the landed
`twig bench discussion --stdin -o json` version-1 contract. Merely opening,
selecting, filtering, changing themes or editing fields does not read discussion.
This is not an offline operation: native admission may attest a cold PAT
principal before the comments GET. It does not refresh work-item cache data,
switch the active item/Bench, post comments, or grant Save/apply authority.
Seeds, uncached rows and ancestor-only context do not acquire discussion targets.

The loopback POST is only an authenticated read-request transport, not an ADO
comment POST. Browser requests contain opaque selection/page handles only. The
server supplies the admitted key, stable Bench and identity and holds the unchanged
native cursor in instance memory. Neither cursor nor provider token is exported
to the DOM, state endpoint, logs, prompts, deep links or persistent storage.
First-page results must match the actual native key/Bench/identity and all
available overlapping origin revisions; subsequent pages must retain the full
native context. The Bench contents digest is native evidence, never fabricated
from a configuration/settings digest. The cursor detects mismatches, not authority:
native admission and its post-read fence still govern every page.

**Next discussion page** performs one explicit read and replaces the displayed
page. There is no automatic paging, retry or accumulated whole-thread claim.
An empty page with `more` is different from an exhausted traversal; neither
exhaustion nor a reported total guarantees a consistent discussion snapshot.
All accepted comment bodies and nullable metadata are displayed as inert text,
including HTML/Markdown and deleted flags. No HTML/Markdown renderer, author
link/avatar, image load or reconstruction of unavailable deleted text is used.
Unavailable body, empty body and literal `null` remain distinct.

Native limits remain 50 entries and 2MiB of provider response, with bounded
continuation tokens. The extension additionally refuses stdout above 4MiB and
diagnostics above 64KiB; these are extension caps, not provider guarantees.
Oversized or inconsistent output fails visibly, never truncates into an empty
or exhausted success. The native child has a 15-second deadline, owned-tree
cancellation and a 19-second closure bound; unresolved ownership prevents reuse.
Native refusals expose only validated safe codes, never raw stderr/provider text.
A failed same-target page read drops Next and labels retained content as a previous
observation; a changed native authority clears it and requires detail reload.
Selection changes, Back, Bench reload, disconnect and close cancel an owned read
and discard the cursor. Generation-qualified invalidation cannot revoke a newer
selection, and late old-target results cannot render under another selection or
a same-item revisit.

This is source integration, not installed-host or live-provider qualification.

## Explicit discussion posting

After **Read discussion (online)** has admitted a published cached member, enter
plain text and choose **Prepare discussion proposal**. The separately named
`/api/discussion-post` route passes the server-held qualified context, exact text
and format to `twig bench discussion prepare-post --stdin -o json`. The native
command rechecks the full context and Bench membership, uses the existing .NET
formatter and v1 append-comment parser, and returns canonical proposal bytes and
their digest. It does not write files, import a journal row, authorize or post.
It also supports restricted Markdown for native callers; the canvas composer
currently submits plain text. Submitted/effective UTF-8 limits remain 16/64 KiB.

The extension retains those exact bytes in a new, exclusively created file under
the physical checkout's `.twig/canvas-discussion/` and acquires the native proposal
preview. This explicit preparation may import a local journal row. The canvas
shows the qualified target, workspace, Bench, identity, native preconditions,
submitted text, effective HTML **as inert text**, digest and blockers. It never
reimplements the native HTML policy or infers eligibility.

Only a separate trusted human click on **Authorize and post exact digest once**
submits the retained digest to the native proposal apply path. It is not exposed
as a canvas agent action. Native append admission and origin/digest fences still
govern the write. Native #532 performs one comment POST and reads back that
specific typed comment ID; a list match, acknowledgment alone or exit zero without
a complete verified receipt is not success. Unknown or unverified outcomes latch
posting for this canvas instance, survive navigation and are available in local
instance outcomes. Inspect the native journal; there is no automatic retry,
autosend, direct backend write or comment-list inference. Proposal files are
retained for journal inspection, not automatically deleted.

This is source integration, not installed-host or live-provider qualification.

## Explicit human-click attachment downloads

For a published cached Bench member, **Show attachments (online)** performs one
qualified native attachment inventory read. It does not read local files or accept
a destination path. Each returned file receives a short-lived, single-use opaque
download handle bound to that exact selected detail; changing selection, reloading
the Bench, closing the canvas, or letting the handle expire invalidates it.

Each **Download** link is a same-origin browser navigation, not a Copilot SDK
action or agent tool. The page refuses synthetic clicks and clicks without an
active trusted browser user gesture. If the host exposes no browser user-activation
API, transfers remain unavailable without breaking ordinary workbench browsing.
The local server requires its loopback Host,
same-origin checks and browser `Sec-Fetch-Site: same-origin` signal, then rechecks
the qualified native item revision and attachment membership before returning the
raw bytes. The browser handles the normal save flow and destination selection;
Twig never chooses or writes a local path. Responses include a validated media
type, safe `Content-Disposition` filename (including UTF-8 `filename*`), exact
`Content-Length`, `no-store` and `nosniff`. Inventory is bounded to 2 MiB and a
download to 16 MiB; oversized, stale, unlinked or foreign-origin responses fail
closed.

This source integration is not yet qualified in the installed Copilot iframe or
against a live ADO attachment. The installed-host check must verify that the
browser's normal save prompt works from the iframe. If the host blocks downloads,
record that exact result; do not silently replace the browser-managed flow. Upload
and readback are explicitly deferred to a second PR.

Discussion editing/deletion and attachment upload remain out of scope.

## Explicit in-session handoff fallback (not host-qualified)

After loading qualified native detail in the workbench, **Prepare handoff brief**
creates an item-bound handle without sending a message. Edit the brief, then click
**Start in this session** to enqueue it in the current Copilot conversation, using
that conversation's current agent and permissions. The brief does not include unsent
field edits. This fallback does **not** create/open a new desktop project session,
launch a separate Agency session, navigate the app, or save/authorize/apply changes.

The server derives the qualified target from its detail observation; the browser
submits only a handle and bounded brief (8192 UTF-8 bytes; 16KiB route envelope).
The local SDK session ID and canonical working directory are checked before sending.
A consumed handle cannot be reused. Admission reports a message ID, not task
completion. Failed/lost send acknowledgements are unknown: inspect the conversation
rather than retry. Late admission evidence is kept under the original target,
including across selection changes. Holds and brief retention are process-memory
only, not durable restart recovery or an atomic work-item/session association.
No ADO link/comment is written. The SDK send acknowledgement has a 10-second bound;
that deadline does not cancel already admitted work.
**Check handoff status** explicitly reads the authenticated server state and reconciles
only the same qualified item, Bench, identity and joined session. It can show late
admission after a lost acknowledgement without resending, retrying or re-arming Start.
It preserves newer brief text; absent matching evidence leaves uncertainty unresolved.

Shipped SDK evidence (`1.0.102-0.unstable.r40029025960.g5a14a15`):
`extension.d.ts` declares `joinSession` for the current foreground session;
`session.d.ts` declares `send` returning an admitted message ID. The installed
Ask Bluebird extension uses this same `session.send` path. `tasks.startAgent`
returns a background agent ID, not an app project session. `CopilotClient.createSession`
is a client-owned runtime API, not a desktop registration/navigation contract.
`CanvasHostContext` declares only canvas-rendering support. No supported combined
desktop launch/association/navigation callback was found in these declarations or
the six installed extension entrypoints. This finding is limited to the inspected
SDK/callback surface; it does not mean desktop session creation links are unsupported.

GitHub's [official deep-link documentation](https://docs.github.com/en/copilot/how-tos/github-copilot-app/open-with-deep-links)
documents `ghapp://session/new` (or `ghapp://session/new/OWNER/REPO`) as a session
creation entrypoint, with app confirmation before creation. Its supported parameters
are `repo`, `pr`, `branch`, `prompt`, and `mode`; `pr` and `branch` are mutually
exclusive. The documented existing-session focus route is plural:
`ghapp://sessions/SESSION_ID`, using a genuine app-local session/workspace ID, not
an SDK runtime, background-agent, message, or GitHub task ID.

The documentation does not declare `host`/`projectId` selection parameters, a
created-session ID return callback, or atomic Twig work-item association. Correct
repository/account host routing and created-session identity remain unqualified.
No guessed routing parameters or singular focus route are implemented here.
The docs prohibit secrets or sensitive user content in prompt URLs: private ADO
briefs must not be put in either a deep-link prompt or its hosted HTTPS launcher.
The documented links are therefore not a qualified private-brief handoff replacement.
The full desktop experience remains a limitation, not a claim of completion.
No link launch, reload or installed-host qualification has been performed for this
fallback.

## Explicit SDK workbench draft actions (source only, not host-qualified)

The registered `twig-workbench` canvas exposes `open_local_draft` and
`stage_local_draft` as explicit SDK actions. Browsing, refresh, detail display and
action declaration never OPEN or STAGE. OPEN first reads the selected member's
qualified Bench detail, then explicitly opens its native local draft; its result
labels whether local observation metadata was committed. STAGE accepts only the
current server-held capture plus exact logical workspace/item, Bench, identity,
draft-generation and revision preconditions and bounded field changes. Caller
preconditions are compare-and-swap checks, not authority. The server does not expose
the opaque capture handle, and mismatches, conflicts, stale origin, unsupported
fields or a missing capture are refused before native STAGE. The transient OPEN
capture is qualified by logical workspace/item, Bench and identity and held only by
the live server instance. Reopening a panel or replacing the provider requires an
explicit new OPEN; it does not recover a server-held capture through `instanceId`.
Durable local draft intent remains in Twig's native workspace store.

Both results explicitly report local-only effects, `authorized: false`,
`published: false` and `canApply: false`. They do not touch the proposal journal,
perform work HTTP, Save, publish, authorize, apply or invoke a signer. This source
slice has not been activated or qualified in a registered Copilot host; current
installed-host and user-store behavior must not be inferred from these tests.

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
256KiB JSON (the native serialized request still has its own 256KiB bound); the
handoff route admits 16KiB and other routes retain 4KiB. Editing rejects duplicate keys, excess depth, unexpected keys
and malformed intent before invoking the adapter.

Only visible, non-contribution, non-read-only `FieldControl` values carried in
full or explicitly empty, with native `editable: true`, receive plain text/null
inputs. HTML, other control types and unsupported contributions remain explicitly
read-only. This is a transport capability limit, not inferred field rules.
After explicit OPEN or STAGE, native `editable: false` fields retain their exact
field reference and reason in the bridge projection. The form displays those
reasons as inert text, never as HTML or editable inputs; the canvas does not
replace them with inferred eligibility.

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
OPEN and STAGE do not authorize or publish. The separate explicit Save route below
is the only workbench action that consumes a retained draft.

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
a fake native adapter, not live native RETAIN composition. Retention itself does
not authorize or save; the distinct Save action is described below.

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
summaries as well as buttons/inputs, text editors and links. The shared additive
textarea/link focus rule uses the same contrast-qualified focus token; it does
not override workbench selectors.

The override belongs to the current document only and resets to Auto when the
iframe reloads. It is not advertised as a durable user preference: ephemeral
loopback origins cannot reliably provide cross-instance browser storage. No
localStorage, native preferences or new endpoint is used. This styling slice
does not qualify actual host rendering. Companion, workbench and proposal review
use the same rendered theme control and semantics. Source tests exercise all
three documents for explicit overrides, host/OS changes, reset to Auto on reopen
and no theme storage or editing/authorization effects. Fallback text exceeds
4.5:1 and focus/borders exceed 3:1 against both fallback backgrounds.

Installed-host visual rendering and interactive host theme changes: **NOT RUN**.
The first smoke timeout recorded for the theme foundation remains a failed
attempt; these source tests do not retroactively qualify it or durable preference
recovery. Feature #1176 and task #1207 must not be closed on that inference.

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
models. The temporary Infrastructure test-project build passed with six SourceLink
warnings, not a supported-solution build.
These receipts are not full CLI, HTTP,
SQLite, installed-host or origin-authority qualification. The capture-only
`draft retain` contract is separate and supplies no semantic review or full
origin; this consumer does not acquire or interpret it.

The complete validated observation is detached and frozen. Details/Back, Show
retained observation, iframe reload and close perform no preview. The protected
loopback state restores the same observation while this provider instance lives;
provider restart or a fresh panel starts empty, not with invented durable recovery.
Retained file/revision/readiness freshness is unknown until another explicit
acquisition. A failed replacement clears old success. Close aborts the owned child
and suppresses late publication. Acquisition and view controls do not apply a
proposal; workbench Save remains separate scope.

### Human-only proposal Accept/Apply (#1212)

After acquiring and reviewing one executable, unblocked native preview, a person
may use the canvas's **Accept and apply** form. There is no `apply_review` SDK
action, authorizer field, steering mode, or rationale input. The form accepts only
the retained capture ID and exact digest from that observation; it also requires
the person's explicit consent checkbox. Replacing the capture clears that consent.
Acquisition, favorable assessments, Details, Show retained observation, reload
and open never apply.

The server binds the request to its retained capture and digest, rejects pending
changes/blockers/non-executable review, and consumes each submitted capture once.
The adapter checks that physical checkout/file paths still match acquisition and
uses the same pinned executable admission and owned-process supervision as local
editing. It invokes only:

```text
twig proposal apply --file <retained-physical-file> --confirm <retained-digest> -o json
```

This server route is reachable through the human review page, not an SDK action.
Native current-digest, origin, revision, policy and journal gates remain
authoritative. The review model is transient and no approval snapshot or
authorizer identity is written to the journal. Existing historical audit rows
remain readable. Review model version alone does not grant proposal
executability; native Apply also rejects unsupported proposal document versions.

Only native exit zero with the exact complete operation coverage, every row
`Verified`, no failure/error and no unsettled acknowledgments is shown as verified.
Partial native rows, warnings, failures and unsettled effects remain visible as
full text-only JSON. Malformed output, cancellation, transport loss or an unproven
owned-tree teardown is an unknown outcome, not rollback or success. No automatic
retry or re-preview occurs. The loopback server and SDK action share single-flight
ownership; closing aborts owned work and suppresses late results.

Iframe reload retains the reviewed observation and Apply outcome while that
provider lives. Provider restart starts empty; the durable native journal remains
the recovery authority, not the canvas's in-memory receipt. Inspect it before
any new attempt after uncertainty. Synthetic Node/HTTP tests are not
installed-app or live-provider qualification.

### Native captured-draft consumption foundation (#1200)

The internal `PlanLifecycleService.ApplyCapturedDraftAsync` consumes the exact
retained digest and canonical journal bytes without exporting a file or creating
an unbound replacement proposal. It shares digest confirmation, current-origin,
policy, process-rule, revision-bound execution and authoritative readback with
proposal Apply. The generic file Apply path still refuses captures.

Immediately before the first write, a single native transaction revalidates the
exact draft generation/effects and served metadata evidence, then wins the existing
`Confirmed` to `Applying` CAS. After verified readback, settlement advances only
that unchanged draft to an empty baseline at the observed revision. Newer edits,
pending work, unavailable clean readback and uncertain remote effects retain intent.
Remote operation verification and local draft settlement are separate outcomes;
recovery never reissues an already attempted mutation.

This foundation supplies no click or authenticated individual attribution. The
explicit action below adds its caller without changing the native gates. Foundation
tests exercise real native SQLite/lifecycle/ADO-adapter composition with external
transport substituted; they do not qualify installed-host attribution.

### Explicit human Save (#1200 clarified contract)

After RETAIN, the workbench displays the exact native capture digest, operation and
nullable effects. **Save exact retained capture (publish)** is a separate human
action, disabled while input is unsent, stale, busy or uncertain. Only its trusted
browser click handler invokes Save; it collects no authorizer identity, mode or
rationale. Synthetic `.click()`, mount, navigation, reload, review and SDK actions
do not call Save. No Save SDK action or agent-facing method is exported.

The browser sends only a single-use opaque handle. The server supplies the unchanged
retained digest, captured caller account and fixed surface to
`twig draft save --stdin -o json`; it accepts no replacement effects or proposal
file. The CLI strictly reads one bounded request through EOF, compares caller
account with current native admission and passes the exact retained digest to
`ApplyCapturedDraftAsync`. No authorizer identity or approval snapshot is recorded.

The CLI compares the captured caller account with the current native admission;
this is not authenticated attribution to an individual. Browser `isTrusted`
keeps ordinary scripted clicks from invoking the UI handler, but it is not a
security credential. Save remains a human-only UI action; the server-held handle
and native digest/origin/journal checks are authoritative.

The held handle is consumed before native execution and revoked on target changes.
The same pinned, bounded, serialized native runner is used for open/stage/retain/save.
Only authoritative Verified operation coverage **and** successful local draft
settlement produce `saved`/exit 0. Verified effects with retained local intent remain
`verified-draft-retained`/exit 1; uncertain effects stay `not-verified`/exit 1.
The panel displays native operation/readback evidence and settlement errors as inert
text, never replays automatically, and holds uncertain outcomes across same-process
navigation. Closing/restarting loses in-memory handles, not the durable native
journal. Inspect that journal after uncertainty; do not infer safe retry from a
missing UI receipt. Installed-app/live-provider qualification remains separate.

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
host/CLI mismatch remains a real, historical failed load, not a passing native integration or
an eligibility/authorization refusal. It is not evidence about any currently installed
binary; current installed-host qualification is unknown. Capability preflight now
refuses such a mismatch before the detail command runs. No retry, installation or reload was
performed. The new review canvas is not yet discovered in that host; no Save,
host authorization or full-native-origin acceptance is claimed.
## Supported platforms

Windows and Linux only. macOS support is temporarily suspended; executable
admission refuses before PATH lookup or native probes, without a Unix fallback.
