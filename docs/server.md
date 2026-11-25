# Twig server

Use [`twig server start`, `twig server status`, and `twig server stop`](commands/system/server.md)
to control the local server, and `twig server endpoint -o json` for read-only
Windows HTTPS endpoint discovery, including before startup.

Install Twig Server separately using the new package and installation paths
below. Earlier packages, configuration names, and runtime selections are not
reused automatically.

**Twig owns the server.** Its source, build, installation tooling, protocol, and
independently versioned releases live in this repository. The optional
[twig-bench-canvas](https://github.com/PolyphonyRequiem/twig-bench-canvas) package
owns presentation assets and licenses, not the server or its durable authority.

The server serves same-machine Benches, native item reads, local draft/Plan
workflows, and human-only Canvas Save/Apply on Windows x64. Linux x64 supports the
headless command host only; there is no HTTP listener or Canvas fallback.
Install the server separately from the CLI before running work-item commands.

### Isolated execution-owner qualification (Windows)

`eng\Test-ExecutionOwner.ps1` runs against build outputs, not an installed server.
Pass the matched `-Server`, `-Cli`, `-CanvasHost` and CanvasHost.Tests `-ProxyHarness`,
the pinned `-OldCli`, `-OldServer`, `-OldCanvasHostDll`, and the thumbprint of an
already trusted localhost `-Certificate`. The script creates a private GUID-named
fixture beneath LocalApplicationData, selects a free port, and removes its own
processes and data. It does not install packages, trust a certificate, or select
a production workspace.

Only this fixture negotiates `local-execution-v2`. Its port, certificate, executable
and canonical metadata home must agree before probing or starting a server.
The OS-held home lease outlives any request timeout. The v1 discovery mirror
and refusal listener exist for old clients, not for executing commands or Stop.
The fixture admits `bench create <name> -o json` and `bench list -o json` in
the owner to demonstrate a shared store; unqualified CLI routes never fall back
to an executable worker. This does **not** activate a release, fence a live old
installation, or qualify an installer for cold cutover.

The matched Canvas subscribes to `GET /api/v2/views/{view}/authorization` before
rendering released partitions. Ready and subsequent cursor changes are registered
under the same release guard as page handoff. An expired grant, denial, stream gap
or disconnect retires the browser buffer. The browser does not persist item text.

Within `benches/{bench}/partitions/{partition}` on that v2 view, a scalar `PUT input`
requires the exact item, field, observed provider baseline, per-field version and write
UUID. The user Bench database keeps the acknowledgement even when a later input
replaces the text. `POST input/inspect` can inspect the original UUID without
preparing an Action. `POST action` supports `find`, `prepare`, `submit`, and `observe`;
these are human content-capability commands, not presentation/agent commands.
Prepare freezes the acknowledged text; Submit names that original write; Observe
does not resubmit. An unchanged flush cannot replace a review. Deliberate re-review
of retained text requires a changed unsubmitted baseline or a zero-dispatch
`ReviewRequired` Action. Private inputs are never inferred from a local-only partition.

In the fixture's `work` directory, `twig update <field> <value> --id <item> --review <uuid> --prepare`
freezes a single update. Use `--stdin` in place of the value to read it once.
`twig update --submit <uuid> --id <item>` submits that review; omitting `--prepare`
on the first command prepares and submits in one invocation. Reusing the same
review with different input is a conflict. An unclaimed review from a prior
daemon boot remains queued; it cannot start a provider write after restart.

In that same isolated workspace, `twig update <field> <value> --id <item> --local`
retains an edit without publishing it. Two reviewed updates to distinct items can
proceed while a third item is edited locally. Admission orders submitted reviews
for the same native item across credentials and mirrors. A blocked submission
returns `Queued`; the same owner wakes it when capacity or a conflicting claim
is released. No client resubmission is necessary. Merely preparing a review
does not reserve the item. If the original effect verifies but the local capture
has changed, observation returns `DeliveryConflict`; it neither resends the update
nor discards the later edit.

ADO and GitHub also accept `twig note --id <item> --text <text> --review <uuid>`
in this fixture. The Twig provider does not implement comments. A review keeps
its original action and submission identities if it yields `Pending` after
three local version conflicts. The same daemon reschedules only the local reducer,
with the original inputs, in batches of up to three CAS attempts. Restart does
not resume its provider call stack. A closed barrier needs original no-send
proof; a post-dispatch observation inspects the original attempt, never resending it.

The qualified pipe admits exactly two provider writers: reviewed scalar `update`
(all three providers) and reviewed `note` (ADO and GitHub). `--prepare`, `--submit`,
`--observe` and `--cancel` change original action state or settle its receipt;
`--find` only reads. `update --local` changes the captured draft, and `bench create`
changes only the Bench catalogue. Neither opens a provider barrier. Qualified
HTTP routes manage profile, enrollment and view sessions or return read-only
data; editor, file-plan, seed, and whole-Plan publication are not qualified HTTP
or pipe routes. The installed command worker is unchanged.

`OwnerUpdateProcessTests` exercises the matched CLI and server with a private
SQLite Twig authority and deterministic HTTP substitutes for Azure DevOps and
GitHub. Each case parks A for 30 seconds and requires B and the local edit to
finish before A returns. The remote-provider cases also verify a comment on B
before A returns. These cases do not qualify creates, installed command routes,
or live provider availability.

If the reply is lost, `twig update --find --id <item>` returns only original
identities and states visible to the current actor, not field values. Multiple
matches are explicit. `twig update --observe <uuid> --id <item>` checks the
original evidence without resending. Ctrl+C detaches the observer;
`twig update --cancel <uuid> --id <item>` cancels only before claim.
A pending or uncertain result carries the original identities and exits nonzero.

**Released Display (owner fixture only).** A negotiated owner content capability
can `GET /api/v2/views/{view}/benches/{bench}/partitions/{partition}/page`.
The route reads the owner-published projection in `released-benches.db` using a
read-only SQLite transaction. It neither discovers membership nor opens the
Bench definitions, source workspace, or provider. An unpublished partition
returns `409 RefreshRequired`, not an empty result.

Each response body is a JSON array of at most 512 KiB. `edit` rows contain only
changed fields, never their provider baseline or type; `seed` rows contain local
intent. The publisher must match the admitted origin's store, connection,
principal and authority stamp. A `provider` partition is separate and requires
a fresh exact item read on its bound connection. Grants live only in the
owner process, expire within 60 monotonic seconds of request start, and cannot
be extended by project access or an in-flight renewal.

Echo `X-Twig-Next-Cursor` as `X-Twig-Cursor` and pin `X-Twig-Generation` on
subsequent pages. A change anywhere in this owner's Bench invalidates the
continuation. On `409 ViewChanged`, discard **all** collected pages; the
fixture reader restarts at most once within a five-second total budget.
`403 GrantRequired`, `503 Busy` and `503 AuthorityUnavailable` carry no item
bytes. Responses identify the authority, process, auth epoch, owner, store,
connection, partition, optional origin, generation, expiry, remaining time and
withdrawal watermark in `X-Twig-*` headers. A successful enqueue precedes
network delivery and cannot be recalled. Clients must still check their
revocation stream before rendering. No HTTP route can mint a grant or supply
release rows; membership and settlement producers belong to the owner.

For an enrolled standalone owner, `GET /api/v2/views/{view}/benches/{bench}/partitions`
lists eligible partition IDs without opening a workspace. The fixture's update
and note paths publish origin-owned intent for exact-pin rules. `update --find
--id <item>` also acquires a separate provider partition for the pinned item
using a fresh exact read. Unsupported local membership or unevaluated workspace
exclusions do not produce a known-empty partition.

Before a qualified workspace CAS, the owner records its predecessor and result
hashes and affected Benches in `released-benches.db`. The matching step ID and
result hash commit in the same transaction as the Core snapshot. Each Bench
retains **all** pending dependencies; no provider call runs inside a publication
transaction. Restart checks each original step against its workspace ledger
before accepting writes. An absent step is no-effect only at its unchanged
predecessor. Inconsistent evidence blocks admission; recovery never dispatches
or reapplies the change. Pages and grants are not restored from an earlier boot.

## Install the release package

From [GitHub Releases](https://github.com/PolyphonyRequiem/twig/releases),
choose a `server-v*` release supporting command IPC and download **both** your
platform's archive and its `.sha256` from that release. A CLI `v*` archive
does not contain a server. Use your normal user account, not sudo.

**Linux x64** (glibc, `/usr/bin/python3` 3.9+, and a mounted `/proc`):

```bash
sha256sum -c twig-server-linux-x64.tar.gz.sha256 &&
mkdir server-package &&
tar -xzf twig-server-linux-x64.tar.gz -C server-package &&
cd server-package &&
python3 -I server-linux.py install server-linux-x64 "$HOME/.local/share/Twig/ServerData"
twig server start -o json
twig server status -o json
twig server stop -o json
```

The installer selects a hash-verified immutable runtime under
`${XDG_DATA_HOME:-$HOME/.local/share}/Twig/Server`. The data and installation
directories must be separate, owner-only (0700), and free of symlinks.
`TWIG_SERVER_INSTALL_ROOT` can select an alternative private root; use the same
value for setup and CLI operations. Stop before updating. Start, status and
stop serialize with installation and authenticate the selected executable,
peer UID, control protocol and durable identity over a Linux Unix socket.
No PID file, certificate, system service, or network listener is required.

**Windows x64** (PowerShell, Python 3.12+ on PATH, and the Windows prerequisites below):

```powershell
$checksum = Get-Content .\twig-server-win-x64.zip.sha256 -Raw
if ($checksum -cnotmatch '\A([a-f0-9]{64})  twig-server-win-x64\.zip\r?\n\z' -or
    (Get-FileHash .\twig-server-win-x64.zip -Algorithm SHA256).Hash -ine $Matches[1]) { throw 'Checksum mismatch' }
Expand-Archive .\twig-server-win-x64.zip .\server-package
cd .\server-package
.\server-install.ps1 -PackageDirectory (Resolve-Path .\server-win-x64).Path `
    -PrivateDataDir 'C:\Users\me\AppData\Local\Twig\ServerData' `
    -Port 7341 -HttpsCertificateThumbprint '<existing-40-hex-thumbprint>' -PythonExecutable python
twig server start -o json
```

Replace the example data directory, port and certificate selection with your
own. Neither package installation nor the CLI creates or trusts certificates.

## Components and ownership

| Component | Owner and role |
| --- | --- |
| `src/Twig.Server` | Twig's executable host (`Twig.Server.exe` on Windows, `Twig.Server` on Linux) |
| `src/Twig.Server.Domain` / `.Application` / `.Infrastructure` | Server model, use cases, and native source/session/transport implementation |
| `src/Twig.Server.Client` | Pure client library for package verification and same-machine startup, without server/provider composition |
| `eng/server-package.ps1` | Standalone, verified self-contained runtime packaging |
| `eng/server-install.ps1` | Source installation and explicit operator selection; optional sign-in startup |
| `eng/server-launch.ps1` | Runtime inventory verification, launch/reuse, and read-only stdio MCP |
| `twig-bench-canvas` | Independently installed presentation package/host/assets |

The server can run without canvas assets. Direct static asset routes are optional
diagnostics and serve only verified packages. Normal canvas opens use the separate
presentation host, which can render before server readiness and forwards bounded
requests from one browser origin. Closing Canvas does not stop the shared server.

## Live-client workspace read

A server view can remember its Bench. The CLI does not share that selection:
`twig workspace --bench MyWork` reads the named Bench without switching a view,
and changing a view cannot redirect a later CLI command.

The `workspace-read-v1` capability in `/api/v1/status` and the authenticated
owner status advertises `GET /api/v1/views/{viewId}/workspace`. Use the existing
owner/profile connection and content bearer; a view ID or presentation capability
alone grants no content access. Older servers must be refused explicitly by clients
requiring this capability, not treated as an empty workspace.

Canvas waits for `ready` on the view's `/events` stream before its first
`/workspace` read and uses the same route after selection changes, on Reload,
and for active reconciliation. It does not fall back to the direct
`GET /api/v1/views/{viewId}/benches/{benchId}` route or to split reads. The two
routes compose the same Bench view shape, but the direct route reads the
requested Bench and does not provide workspace-level consistency guarantees.

The source-generated `NativeWorkspaceDto` response has these camel-case fields:

| Field | Existing native contract |
| --- | --- |
| `viewState` | `NativeViewStateInfo`: store/profile identity, saved choice revision, retained source recovery, current Bench and authority expiry/renewal times |
| `summary` | `NativePanelSummary`: native catalogue revision and definitions, registered connection labels/availability, selected contexts, repository bindings and verified runtime context |
| `session` | `NativeSessionInfo`: current Bench and presentation preferences from that same profile observation |
| `view` | Selected `NativeBenchViewDto`, or `null` when no existing Bench is selected; includes source-qualified items, protected drafts, source stamps/errors and existing Plan status/review digest |
| `planOperationId` | Original in-memory Plan operation ID for this attachment's selected Bench, if available; observe using the existing `GET /api/v1/views/{viewId}/plan/operations/{operationId}` |
| `selectionError` | Explicit `Failure` when the selected Bench was deleted; catalogue and saved selection remain readable, with `view: null` |
| `reconcileAfterSeconds` | `30`: maximum active-client interval between authoritative workspace reads |

This is a bounded projection, not a transaction across providers or a new
publication authority. It reuses the initial-view assembler and native
source projection. It restores saved choices on first attachment and when
another attachment changes the profile's source/Bench choices; ordinary reads
do not reset an explicitly selected Bench, editor ticket or pending operation.
Catalogue, registry, repository binding, profile, selection (including a
switch away and back), local snapshot/exclusion and Plan revisions are
rechecked. Concurrent changes refuse the read with `409` rather than releasing
mixed selection/content. Sign-out, capability replacement and expiry still
refuse content. Failed source reads remain explicit per-source failures, not
fabricated empty membership; an admission change during final checking can
refuse the entire read.

Native source reads wait for a busy checkout operation lease within their
existing cancellation/deadline budget, before opening stores or contacting a
provider. A brief overlapping operation is not itself a connection failure.
After acquiring the lease, the read performs the usual fresh authority and
access checks; cancellation and deadline exhaustion still withhold content.
This only waits for admission and never retries an operation or replays a write.

Initial-view loading retains its single full local observation per restored
source and final admission/profile/Plan checks. Only the authoritative workspace
read adds the final local snapshot/exclusion reread. The workspace read module
owns restoration, observation reuse and the final check for both read kinds.
Bench selection belongs to the view and its saved profile choices, not the
source worktree. Opening a source verifies its native authority without reading
or writing a Bench preference.

Workspace acquisition has a 30-second ceiling, uses existing per-source budgets
and at most eight concurrent source acquisitions, and performs only local
snapshot reads under retained exact-source admission. Merely registering a
source does not authorize it: absent or denied admission cannot trigger schema
refresh or provider discovery. No saved query is executed upstream. Source
cache versions, schema fingerprints, observation times, offline limitations and
partial failures remain visible. Explicit refresh/recheck, lazy details, seed
publication evidence, discovery and typed mutation endpoints remain unchanged.
`planOperationId` and `view.plan.lastReviewDigest` identify original work
for observation, never for replay. `lastReviewDigest` identifies an admitted
attempt, not a preview. Across attachment loss, inspect the original attempt
and its journal receipts.

### Repository sources in Canvas

For a repository view, `summary.repositorySetup.connections` describes the
selected Bench's sources in that repository, not just connections saved on
the Bench. Use `declaredSourceLabel` and `declaredSourceStatus` to explain a
repository declaration that could not be resolved. Do not prompt for a
second Bench attachment to resolve a repository dependency. An available
source with zero items is a valid empty result, not an incomplete setup.
The view admits up to 64 sources, giving saved Bench dependencies priority.
Additional repository sources remain listed in setup as `ViewSourceLimitReached`.

### Canvas Plan Apply contract

For the selected Bench, read `GET /api/v1/views/{viewId}/benches/{benchId}/plan`.
The response's `stagedVersion` binds the Bench, Plan and staged captures to the
client's next explicit Apply. It is absent unless the enabled Plan has ready
scopes with their original connections attached and a staged snapshot is available.
Status and the original attempt digest remain readable without those connections.
Send `{ "benchId": "...", "stagedVersion": "..." }`
to `POST /api/v1/views/{viewId}/plan/apply`, then poll the operation ID returned
with HTTP 202. Do not show a confirmation prompt or automatically resubmit
after a stale-version conflict or lost response. Reload staged intent after a
conflict; check the original outcome after an interruption.

`POST .../plan/review` remains optional presentation and neither retains an
attempt nor grants Apply. Review/observe output no longer has `ticket` or
`canApply`. For an admitted attempt, `POST .../plan/observe` reads it and
`POST .../plan/continue` continues verified delivery only; both take
`{ "benchId": "...", "digest": "..." }`. The old `ticket`, `digest` and
`deliveryOnly` Apply fields are not accepted. Authentication, view ownership,
CSRF and source admission still apply.

The protocol identifier remains `twig-bench-service-v1` during development.
Canvas clients that call Plan Apply must use the contract above; the protocol
identifier does not imply support for the retired preview and confirmation
routes. Before releasing, validate the separately shipped `twig-bench-canvas`
against the current server, including stale staging, navigation, lost responses,
and delivery-only continuation where the client uses Plan Apply.

Connect the authenticated SSE stream **before** the first workspace read. The
server registers its bounded subscriber before flushing `event: ready`. On
**every** `ready`, including reconnect, clients must read the workspace.
`changed`, `presentation` and same-view `agent` events are advisory invalidations,
not patches. A full single-entry subscriber queue coalesces to
`event: changed` with `data: {"resync":true}`. This unscoped, content-free signal
invalidates **all** attachment resources, including agent state; it cannot lose
an earlier resource invalidation when a different resource changes. Existing
clients still recognize `changed`. Delivery rechecks view authorization, and
the stream cannot outlive its original authority lease.

Clients should run one dirty/read loop: coalesce invalidations, and if an
invalidation arrives during a read, read again after it completes. Reject late
responses after attachment/resource changes. Reconcile at least every 30 seconds
while active and on activation to cover CLI writers and commit-before-notify
failure. Activation must check attachment validity and real stream progress;
replace a stalled stream and refresh. Reconnect read-only streams with bounded
backoff and jitter; authentication/authorization/protocol refusals require
explicit recovery. Apply command results or reread immediately without waiting
for SSE, and never retry an uncertain write automatically. SSE does not observe
upstream changes until an explicit native refresh has observed them.

## Private recoverable editor inputs

`private-editor-inputs-v1` advertises the following **human owner-only** contract.
It does not change `workspace-read-v1`, `planOperationId`, native publication,
or the meaning of Save, Stage, Create, Review or Apply. Clients requiring input
recovery must explicitly refuse older servers, not fall back to browser storage.
The canvas switch is a separate delivery phase; no browser inputs are imported.

Fresh `POST editor` responses contain `EditorFormDto.inputs`.
`POST editor/relationships` returns it at `fields.inputs`, after selecting the
explicit endpoint through the existing native route. New seed and successful
Stage responses also carry this projection. Evaluation responses do not replace
the input acknowledgement. All paths below are relative to
`/api/v1/views/{viewId}/`, use the existing owner content bearer and
`X-Twig-Bench: 1`, and require the current native ticket:

| Method and route | Source-generated request DTO |
| --- | --- |
| `PUT editor/inputs` | `EditorInputWriteBody { ticket, key, expectedRevision, writeId, values }` |
| `POST editor/inputs/observe` | `EditorInputObserveBody { ticket, key }` |
| `POST editor/inputs/discard` | `EditorInputDiscardBody { ticket, key, expectedRevision, writeId }` |

These return `EditorInputRecoveryDto { key, revision, writeId, status, values }`.
`key` is server-derived, not client-created authority. `writeId` is the UUID of
the last accepted PUT/discard for this buffer (preserved on consumption), or
`null` for an absent/reclaimed buffer. Status is `Empty`, `Restored`, `Conflict`,
`Discarded` or `Consumed`. Only `Restored` releases `values`; `Conflict` retains
the inputs privately but withholds them from restoration until explicitly
discarded. A fresh scalar opening restores only its scalar/seed buffer; pair
recovery requires choosing the exact endpoint again. There is no cross-client
resume inventory or implicit selection of another buffer.

`EditorInputValues` is `{ fields, links?, adding? }`:

- `fields`: the existing `FieldDto[]` shape, `{ reference, value: { kind, value,
  dataType } }`. Numeric and instant `value`s remain strings, including incomplete
  `""`, `"-"` and `"12."`; persistence checks shape/size, **not** native evaluation
  or required/allowed/read-only policy. Native submission still converts and
  validates them normally. Raw fields are unsupported.
- `links`: for normal ADO pairs only, an optional array of existing
  `RelationshipIntentDto { endpointId, relation, outgoing, remove }` choices
  from that exact pair. Empty/incomplete selection is retainable, not staged
  debt. Scalar buffers omit `links`. No relationship is inferred.
- `adding`: optional boolean for a qualified LOCAL-only pair; fields must be
  empty and links absent. It is not a review, digest or permission to Apply.

Records are isolated by the owner-local profile/catalogue store, exact native
source binding/root (including principal/authority), repository context, Bench,
source-qualified item, execution store, Plan identity/scope, scalar/normal-seed/
ADO-pair/LOCAL-pair mode and explicit endpoint. Tickets and views are not part
of durable identity. Independent live buffers cannot silently merge: the
conditional input revision must match. A never-written buffer starts at zero;
each record advances independently, so first writes to disjoint buffers do not
conflict. Use the returned revision, including on tombstones. Never adopt the newest revision
and retry automatically. Input CAS failures use `InputRevisionConflict`; a
baseline change refuses replacement with `InputBaselineConflict`. Existing
profile-lease contention can return `ViewStateBusy`. Observe/reconcile rather
than treating any of these as permission to overwrite.

The existing profile lease and flushed atomic writer persist at most 64 buffer
live buffers and 1 MiB UTF-8 per profile. Each value payload is at most 256 KiB UTF-8,
with 64 unique fields, strings of at most 65,536 characters, string lists of at
most 256 elements, and 128 explicit links. The PUT transport bound is 256 KiB
plus 16 KiB framing; existing native command body bounds remain unchanged.
Discard/consume clear values, retaining bounded acknowledgement tombstones.
Cleared buffers free live capacity but retain their record revision; a delayed
first write cannot recreate discarded values.
No acknowledged live inputs are evicted. `InputLimit` is an explicit refusal.

Recovery always authenticates/admit-checks the exact source and acquires a fresh
native editor. The server compares native schema/field metadata, observation
revisions/values, owned draft/capture fingerprints and revisions, Plan binding,
and, for pairs, endpoint draft evidence, relation choices, intent and incident
edges. LOCAL edge versions detect ABA. Cache-read-only version increments do
not conflict with input recovery; the newly captured native LOCAL ticket still
enforces its own fresh store CAS. `inputBaseline` on a form is opaque native
evidence, not client comparison logic, a write precondition or publication
authority. No old ticket, grant, review or digest is restored.

Autosave performs no provider write, native field evaluation, staging, review
or publication. Raw records never appear in workspace, generic agent or MCP
reads, nor SSE. Denial, sign-out, expiry and source/context changes withhold
them. They are local recovery data, not a second domain store or outbox.

### Canvas usage example

Keep typing in memory and debounce a **single outstanding conditional PUT**:

```javascript
const form = await api("/editor", "POST", { sourceId, itemId, benchId, planMode: false });
let acknowledged = form.inputs; // Conflict: stop; offer explicit discard, not JS rebasing.
const values = { fields: [
  { reference, value: { kind: "Integer", value: "-", dataType: null } }
] };
const pending = { ticket: form.ticket, key: acknowledged.key,
  expectedRevision: acknowledged.revision, writeId: crypto.randomUUID(), values };
acknowledged = await api("/editor/inputs", "PUT", pending);
// Only now show "Inputs saved locally". Newer keystrokes are still memory-only.
```

If PUT/discard times out, retain its original `{ key, writeId, expectedRevision }`
and observe `POST editor/inputs/observe` with the current ticket/key **before any
further attempt**. After view/server loss, authenticate and open the exact editor
and endpoint afresh first. Match the returned `writeId` to acknowledge the
original write; a different ID/newer revision is explicit concurrent conflict,
not permission to replay or overwrite. An unchanged observation is not a
guarantee that a late request cannot arrive: any deliberate subsequent write
uses a new UUID and a condition, never a blind replay. Retain unsaved memory
visibly until its own acknowledgement. No shutdown or `beforeunload` write is
required for correctness.

Save/Stage first flushes the intended revision, then submits **exactly** its
payload through the existing typed native operation, adding
`inputs: { key: acknowledged.key, revision: acknowledged.revision }` to
`EditorChangeBody` (`editor/save`, `editor/stage`) or `RelationshipChangeBody`
(`editor/relationships/save`, `editor/relationships/stage`). The latter keeps
existing `intent: acknowledged.values.links`, not a new command shape.
LOCAL `editor/local/review` accepts the same optional reference on
`LocalRelationshipReviewBody`, alongside the exact `adding` boolean.
The server rejects payload/reference mismatch with `InputRevisionConflict`
before the native operation. Successful Stage or LOCAL Review consumes only
that revision; Save consumes it only when verified with no retained draft.
Newer concurrent revisions survive. References remain optional for existing
native clients and never bypass native guards. Apply and original journal/
receipt/seed-publication observation routes are unchanged.

A lost native Save/Stage/Review/Apply response must be resolved through original
native work/evidence, not by resubmitting raw inputs. A consumed input is not
proof that a provider write occurred. For Cancel, conditionally discard the
applicable unsubmitted buffer, observe any uncertain discard, then close the
current transient ticket with `editor/close`. Close alone preserves recovery
inputs (for navigation). Neither route deletes native drafts, staged debt,
captures, attempted effects, journals, reviews or receipts.

## Windows prerequisites

- 64-bit Windows, running as the intended owner.
- An existing dedicated **private data directory**, separate from the installation.
- An explicit stable localhost port, from 1 through 65535, **not 443**.
- An existing trusted `localhost` server certificate in **CurrentUser\My**, with
  private key, server-auth usage, and its exact 40-character thumbprint.
- For source installation/packaging: a clean exact Git checkout, the SDK selected
  by `global.json`, and an existing Python interpreter.

The tools do **not** create or trust certificates, open remote access, enroll
provider credentials implicitly, or repair machine-wide prerequisites.
Loopback does not mean anonymous access: same-owner Windows sign-in, trusted
pinned TLS, durable server identity and protocol checks remain required.

## Install from source

From a **clean** checkout, using an already prepared private directory and
certificate (replace these example values):

```powershell
.\eng\server-install.ps1 `
    -PrivateDataDir 'C:\Users\me\AppData\Local\Twig\ServerData' `
    -Port 7341 `
    -HttpsCertificateThumbprint '<existing-40-hex-thumbprint>' `
    -PythonExecutable python
```

Default installation is `%LOCALAPPDATA%\Twig\Server`; the independent canvas root
defaults to `%LOCALAPPDATA%\Twig\Canvas`. `-InstallRoot` must remain a dedicated
directory beneath the current user's LocalApplicationData. Paths must be explicit
local paths, not network shares, drive roots, or reparse-point aliases.

The installer publishes only the server, untrimmed and self-contained, under
`versions\<source-sha>`. `runtime.json` inventories file hashes and protocol.
`current.json` selects that runtime together with private-data, port, certificate
and canvas-root settings; a replaced selection is retained in `previous.json`.
Do not hand-edit these records to bypass verification.
These records are private implementation details, not a consumer discovery API.
Independent consumers use [`twig server endpoint -o json`](commands/system/server.md#endpoint-discovery)
for the verified origin, certificate pin, opaque selection change token and
nullable prestart durable identity. Revalidate after startup and retain the HTTP
same-owner, TLS, durable identity and protocol checks; discovery alone is not readiness.

Installation starts/stops **no process** and installs no canvas package.
`-RegisterStartup` is explicit opt-in and creates the user's `Twig Server.lnk`
sign-in shortcut. This is a per-user process, not a Windows Service installation.
Foreign installation entries or Startup shortcuts are refused rather than replaced.
When a desktop host omits `SystemDrive`, the Twig CLI restores it for the
Windows launcher so Windows Shell does not write its property cache into the installation.

## Start, reuse, and stop

After installation, run from any directory:

```powershell
twig server start -o json
twig server status -o json
twig server stop -o json
```

Start returns `{"running":true}` only after the selected server passes its
authenticated readiness check. Status does not start anything; a stopped
server returns `{"running":false}`. A missing installation, an unavailable
locked instance, or a listener whose identity cannot be verified is an error,
not a stopped result. Stop requests graceful shutdown through a per-process
Windows event restricted to the current user, not a PID, HTTP route, or MCP
tool. It confirms shutdown before returning `{"running":false}`. No command
installs, upgrades, force-kills, or changes the sign-in startup selection.

The CLI uses `%LOCALAPPDATA%\Twig\Server`. To select a custom installation,
set `TWIG_SERVER_INSTALL_ROOT` to the same absolute local path under
LocalApplicationData used for `-InstallRoot`. The verified installation record
selects the private data directory; it is not inferred from the current workspace.

Equivalent direct invocation in PowerShell:

```powershell
$launcher = Join-Path $env:LOCALAPPDATA 'Twig\Server\server-launch.ps1'
& $launcher -EnsureRunning
& $launcher -Status
& $launcher -Stop
```

`-EnsureRunning` verifies the selected runtime, exact Windows owner, trusted TLS,
server identity and protocol, then reuses an existing healthy process.
Only actual connection refusal permits serialized startup with the exclusive
instance guard. An existing unavailable process or uncertain startup is not
silently killed, restarted, upgraded, or replayed. Success returns
`{"running":true}`.

For an operator console instead:

```powershell
& $launcher
```

The console prints the localhost endpoint and accepts **`stop`** for explicit
shutdown. Do not start a second console against a running instance's data.
Canvas clients and MCP EOF close their view/session, not this shared process.

The server refreshes process/type metadata for already-admitted registered
connections every **15 minutes**, including overdue metadata after startup,
without requiring an open Canvas. A one-minute sweep checks each connection's
last successful schema observation; failed attempts are spaced at least 15
minutes apart during that server run. Successful discovery atomically replaces
the retained definitions and notifies open views. Ordinary Bench reads do not
perform metadata discovery.

This is provider-read-only: it does not migrate work-item types, change Bench
membership, or authorize new connections. A failed or canceled schema refresh
keeps the previous definitions and their original observation time. A failure
limited to schema discovery does not establish work-item access loss. Known
binding denials and changes still block cached display; scheduled work cannot
clear a block. Background discovery uses the same
per-source coordination and bounded native operations as foreground requests.

Explicit online admission or **Check connection** can replace an unreadable
schema cache after observing fresh metadata for the exact recorded source and
principal. If an existing schema is still readable, a failed refresh leaves it
in place. An unreadable schema is not usable until replaced.

The server no longer uses the `.check` grant. A legacy cache requires a fresh
Twig workspace; schema discovery cannot readmit it, and the old grant state is
neither interpreted nor migrated. Offline display does not make
provider calls or claim current upstream access. Known source denials withhold
historical content for that binding. An exact item refusal or absence observed
by delegated reads, editor reads, or CLI and planning acquisition withholds that
item's cached representation and related references. A timeout, partial response, or unrelated query
omission does not withdraw an item. Schema and roster success do not clear a denial.
Direct CLI historical `show`, `show-batch`, `tree`, and `workspace` output honors
the same recorded denials. The CLI and server use the same per-user evidence
directory even when the server uses a custom `--data-dir`. Exact CLI `show --refresh` and `tree --refresh`
record explicit provider refusals and restore only the item successfully
fetched and retained. A source-wide denial is not cleared
in place: after resolving provider access, initialize and register a fresh
workspace. Do not use a fresh workspace to infer the outcome of an old write.
The underlying mirror and recovery journal are not rewritten, and data already
delivered cannot be recalled.

Saved-query and backlog browse responses project the exact acquired provider
page, without adding draft overlays or links to items outside that page.
Delegated item and Bench reads use fresh provider fields for remote work items,
without falling back to the local mirror if the provider refuses or cannot
complete the read. Local draft edits remain separate from provider fields.
Delegated Bench reads verify the full membership before returning a count and
are limited to 100 remote items; larger Benches can still be read one item at a
time.

To select a newer source build, run the installer from a different clean commit.
Version directories are immutable; reinstalling the same selected SHA is refused.
Selecting new files does not replace a running instance. Coordinate explicit
shutdown and the subsequent launch; do not infer that installation restarted it.
A UI-only package update must not reinstall/restart the server or invalidate
compatible sessions.

## Packages and releases

To produce an uninstalled package from a clean checkout:

```powershell
.\eng\server-package.ps1 -Output "$PWD\artifacts\server-win-x64" -PythonExecutable python
```

The output directory must be new and beneath this checkout's `artifacts`.
Its final directory name must be `server-win-x64`. The command also writes
`twig-server-win-x64.zip` and its `.sha256` beside that directory, including the
independent launcher, installer and owned-process supervisor. It refuses to
overwrite an existing runtime or archive. Packaging verifies the complete runtime
inventory without certificate, Startup, installation, or process changes.

The [Server package workflow](../.github/workflows/server-release.yml) packages
Windows and Linux artifacts independently of the CLI. Pull requests do not start package
builds; use a manual workflow run for an optional hosted package check (see
[GitHub Actions](architecture/build-and-release.md#github-actions)). `server-v*` tags publish
`twig-server-win-x64.zip` and `twig-server-linux-x64.tar.gz`, each with a
SHA-256 file. Both checksum files use the `sha256sum -c` compatible
`<lowercase-sha256>  <archive-filename>` format. Both archives include operator installation
tooling; see [Install the release package](#install-the-release-package).

Each server workflow build also produces a **separate, unsigned CLI candidate**
from the same source commit. The gate extracts both archives, verifies their
checksums, and exercises isolated command IPC without source imports or an SDK
at runtime. Windows additionally exercises the extracted installer with only its
external-command/certificate boundary replaced by a synthetic fixture. It checks
fresh installation, immutable reinstall refusal, upgrade selection, retained
rollback-record validity, refused upgrades and endpoint/inventory guards.
This is not a trusted HTTPS, certificate-store, Windows owner-authentication,
Startup or hosted Canvas qualification. No live rollback is performed.

The CLI candidate is an Actions artifact, **not** a CLI release. The server
release job downloads only `twig-server-*` artifacts, never the CLI candidates.
Releasing the server does not update the CLI; releasing the CLI does not install
a server. See the [paired publication gate](architecture/build-and-release.md#paired-distribution-readiness).

## Windows owner default-profile recovery

`GET /api/v1/windows-profile` requires same-owner Windows authentication and
returns only `{ "storeId": "…" }`. It does not resolve or modify a profile.
If profile resolution returns `DefaultProfileUnavailable`, an explicit recovery
action may send `POST /api/v1/windows-profile` with `recoverMissingDefault: true`,
the exact current `storeId`, and no old `profileId` (or `profileId: null`).
Use discovery only when the browser has no saved store ID. Do not silently
substitute the discovered ID for a retained stale ID, and do not retry a
`StoreReplaced` response automatically. Passive renewal never recreates missing
profiles. Recovery preserves valid or signed-out defaults and leaves corrupt
profile documents untouched.

## Security and data ownership

On Windows, command-pipe access allows only the server's token user and explicitly
denies network logons. Pipe owner metadata uses `Token.Owner` for .NET
`CurrentUserOnly` compatibility; the DACL still allows only `Token.User`.
The client retains `CurrentUserOnly` and identification-level access, and verifies
the connected server process's `Token.User` against its own before writing any
Invocation bytes. An unavailable PID, token or user fails closed without dispatch.
Private-directory ownership and endpoint selection checks remain user-scoped.

The server's private data holds durable server identity and presentation/native
evidence. User-wide Benches live separately in `benches.db` beside the native
registry. Items from the Twig provider live in explicitly named databases under
`native-databases/` there, not in the server directory or a workspace mirror.
Twig connection requests carry a `database` name; `setup/initialize-twig` also
requires `database`. Names use 1-64 lowercase letters, digits or internal hyphens.
Two requests for different database names are different connection requests;
neither an owner-wide default nor the old singleton database is inferred.
The separately packaged presentation client must supply that name.
`TWIG_USER_HOME` selects the metadata home; changing only `-PrivateDataDir` does
not isolate native databases.

Benches retain exact source dependencies, including sources with zero members.
Missing or ambiguous sources produce a degraded view, not fabricated empty
membership. Healthy sources remain usable. Repository bindings can constrain
reads without changing shared Bench intent or draft destinations.

The installed Bench host authenticates the Windows owner and initializes scoped
agent reads, local Plan drafting, pin/unpin and saved-rule editing for the exact
profile/admitted sources. Explicit sign-out and restrictions override this.
An SDK acknowledgement discloses no human content capability and grants no
Save/Apply authority. New connection requests require owner-approved setup;
credentials, accounts, and paths are not chosen by the requesting agent.

Provider/source/principal, profile, policy, revision, digest and native journal
checks still govern changes. **Save/Apply and provider publication are human-only
in the server host.** Local staging is not publication; an uncertain result retains
its original capture and attempt without creating another write.

Old developer-local application data is not automatically migrated or adopted.
Use explicitly scoped fresh state when needed, but never delete credentials,
source checkouts, upstream items, or unknown-write evidence as a reset shortcut.

## Managed ADO connection setup

Standalone/global Connections setup does **not** require a source checkout or a
user-supplied local Git folder. After explicit Microsoft enrollment and native
ADO binding, hosts can use `NativeConnectionSetup.InitializeManagedAsync` with
`NativeAdoSetupTarget(organization, project, bindingId, team?, gitProject?)`.
The existing `INativeConnectionSetupGateway.InitializeManagedAsync` forwards the
same target. Organization/project and optional team/Git project retain the fresh
setup semantics; the exact registered binding is mandatory, never an endpoint
default or permission to switch accounts.

The server advertises `managed-ado-setup-v1` in its status capabilities.
Owner-authenticated views may call:

```http
POST /api/v1/views/{viewId}/setup/initialize-managed-ado
X-Twig-Bench: 1
Authorization: Bearer <this-view-content-capability>
Content-Type: application/json

{"organization":"example","project":"Project","bindingId":"<exact-native-binding>","team":"Team","gitProject":"Git project","epoch":0}
```

`SetupManagedAdoBody` contains these fields only; `team` and `gitProject` may be
omitted/null, and `epoch` defaults to zero. There is no workspace/store path.
Use the current setup epoch, not a stale navigation value. This is the existing
bounded `202` setup-operation contract: observe the returned `operationId` at
`GET /api/v1/views/{viewId}/setup/operations/{operationId}`, or cancel that
original operation with `DELETE` and `{}`. Observe failures and uncertain effects;
do not automatically resubmit setup after a lost response.

Native code allocates a private context beneath `<native-metadata-home>\sources`
with a private Git anchor as an implementation detail. Git must be available.
`NativeManagedSetupResult.WorkspaceRoot`, and HTTP Ready's
`SetupOperationDto.Source.WorkspaceRoot`, identify that native-owned context for
subsequent reads; clients must not invent, relocate or manage it. Initialization
uses the same exact binding, principal, authority, policy, store creation,
schema and online-read checks as fresh setup. Ready requires successful readback.
Cancellation/failure retains typed native effects and possible partial storage,
with no fallback identity, retry context or tracker publication.

Successful registration appears in the native connection library
(`UserBenchStore.WorkspacesAsync`, exposed by the view's `GET .../benches`).
It can be opened through the returned context without any source checkout.
Registration neither adds Bench membership nor assigns a repository binding,
changes active context, or grants Save/Apply authority.
`InitializeFreshAsync(NativeFreshSetupRequest)` and
`POST .../setup/initialize-ado` remain the separate **repository-scoped** path:
the caller deliberately supplies an existing fresh Git worktree root.
Managed setup does not migrate or adopt developer-local data.

## Read-only MCP

The server advertises `mcp-read-v1` on
`POST /api/v1/views/{viewId}/mcp`, on the same authenticated localhost origin.
There is no second listener. This is **not** the retired `twig mcp` /
`twig-mcp` application.

First verify/start the selected server with `-EnsureRunning`. Then configure
your MCP client's stdio server with this shape, substituting the **actual absolute**
installed launcher path:

```json
{
  "command": "powershell.exe",
  "args": [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-File",
    "C:\\Users\\me\\AppData\\Local\\Twig\\Server\\server-launch.ps1",
    "-Mcp"
  ]
}
```

Wrap this object in the server configuration required by your client.
Do not rely on environment-variable expansion in JSON or hardcode a versioned
executable. `-Mcp` and `-EnsureRunning` are separate invocations, not combined flags.
The bridge verifies the existing selected server and registers its own scoped
view. Stdio contains MCP JSON-RPC, not operator logs or capability secrets.
EOF closes that view; failed requests are never replayed.

| Tool | Contract |
| --- | --- |
| `read_bench` | Selected Bench members, exact snapshot/item identities, available field references and unavailable-source metadata |
| `show_work_item_details` | Uses returned identities and up to five exact field references; optional `assignedToField`, `focusField`, and explanatory `note`; item values are never caller-supplied |

Details distinguish cached observations, provider revision, unpublished items,
local plans and unresolved references. Unknown fields, stale snapshots,
revoked access or an unavailable selected source refuse explicitly. An unrelated
missing source does not block a healthy item's details. If no Bench is selected,
choose one in Canvas; MCP does not change selection.

The `ui://twig/work-item-details` MCP App resource belongs to the separately
installed, hash-verified canvas package. Plain text/structured reads do **not**
require those assets; rendering the resource does. No draft, membership, Save,
or Apply tool is exposed by MCP.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Runtime hash/inventory refusal | Check the selected clean package and manifest; do not disable verification or overwrite immutable files |
| Certificate refusal | Check the existing CurrentUser certificate, private key, localhost identity, server-auth usage and trust; the launcher will not alter trust |
| Port/process conflict or unavailable existing instance | Inspect the exact selected operator endpoint/process; do not automatically restart an uncertain instance |
| Degraded Bench | Inspect its admitted source dependencies and provider access, rather than replacing membership with an empty set |
| MCP will not connect | Run `-EnsureRunning` separately; verify the absolute installed launcher path and owner context |
| Details work but the MCP App does not render | Verify/install the independent compatible canvas package; do not reinstall the server as a UI repair |
| Unknown Save/Apply outcome | Retain and inspect the original capture/digest/journal; do not repeat publication |

For current implementation boundaries see the
[architecture overview](architecture/overview.md) and
[clean-core contract](architecture/clean-core-contract.md).
