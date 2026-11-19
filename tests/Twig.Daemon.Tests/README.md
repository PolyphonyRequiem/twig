# Provider-owned daemon invariant tests

These tests adopt backend assertions from `PolyphonyRequiem/twig-bench-canvas`
at `f154a3502f2458cd328e51b71802ae108bbfa12d`, without a Canvas checkout,
presentation assets, browser transport, or the historical test assembly identity.
Existing `Bench.Service.Tests` friend access remains during the consumer cutover;
the new tests use `Twig.Daemon.Tests`. No production API was made public for tests.

## Runnable ownership

| Original Canvas source | Twig source / selector | Retained assertions |
| --- | --- | --- |
| `tests/Bench.Catalog.Tests/Program.cs` | `tests/Twig.Daemon.Catalog.Tests/Program.cs` (20 console cases) | Identity replacement, canonical uniqueness and concurrent CAS, receipt replay across reopen and commit-without-response process exit, typed selectors, source-qualified projection, unknown membership, drafts/mappings/tombstones, admission and offline guards |
| `tests/Bench.Sessions.Tests/Program.cs` | `SessionInvariantTests` (6 xUnit cases) | Repository selection, linked worktrees and restart; initial-view acquisition/revision guards; owner transport and exact lifetimes; durable profile CAS, restoration, sticky signout and store identity; cached display/schema fidelity |
| `tests/Bench.Service.Tests/Program.cs` adapter case table | `NativeSourceAdapterInvariantTests` (21 xUnit cases) | Admission/reselection, shared-root coordination, transient recovery, concurrent additive changes, exact schema/cache retention, durable denial under IO faults/cancellation/restart, explicit reauthorization and identity replacement |
| `NativePanelTests.cs` | `NativeHttpInvariantTests.ProtectsNativePanelRoutesAndProfiles` (2 cases) and `NativePanelTests.cs` | Native route/SDK denials, exact account DTO, profiles, native context errors/recovery, presentation revocation, source-scoped degraded content, planning and team isolation, shared edits, stale CAS, protected drafts/exclusions and 300-pin request roundtrip |
| `NativeAggregationTests.cs` | `NativeHttpInvariantTests.AggregatesOnlyAdmittedNativeSources` | Cross-source identity, explicit contexts, mapped UUID/exclusion HTTP projection, scoped denial, unavailable pinned seeds, parallel source acquisition and repository restrictions |
| `ConnectionSetupTests.cs` | `NativeHttpInvariantTests.ProtectsConnectionSetup` (4 cases) | Owner/SDK/CSRF/origin/input/epoch gates; operation cancellation and late completion guards; structured failures; creation-bound readback |
| `NativeConnectionLabelTests.cs` | `NativeAccountLabelInvariantTests.LabelsOnlyTheExactLocallyBoundAccount` | Exact bound AAD/GitHub identity, tenant-based account kind, no cross-account substitution; local-only label lookup and GUID/path/credential/provider/input bounds |
| `NativeSourceDiagnosticsTests.cs` | `NativeHttpInvariantTests.BoundsAndRedactsSourceDiagnostics` | Per-source/set deduplication, safe counts, truncation, bounded memory/log entries and concurrency |
| `NativePanelTests.cs` diagnostic/process gateway modes; `native-diagnostics-browser.mjs`, `schema-refresh-browser.mjs`, provider metadata from `native-process-browser.mjs` | `NativePanelTests.ReadComposition.cs` / `NativeReadCompositionTests` (2 xUnit cases) | Actual native adapter, production HTTP host and logger filter: redacted DTOs, per-context deduplication/change/reappearance/truncation; exact retained schema, explicit discovery, unavailable roster and recovery without changing source/items/Bench definitions |
| `McpBridgeTests.cs` | `NativeHttpInvariantTests.ScopesMcpBridgeAuthorityAndReleasesItsView` | Exact owner bootstrap, view-scoped forwarding, protocol-only stdout, notification suppression and EOF cleanup |
| `PresentationRouteTests.cs` | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.PresentationHttp.cs`, `NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority` | Real native transport/capture counters; held-provider response under two seconds; complete persisted choices; genuine cross-view CAS/reconciliation; SDK attachment/layout authority; zero publication/selection writes; content-priority invalidation in both queue orders |
| `AdoEditorTests.cs` startup instrumentation used by `bench-startup-browser.mjs` | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.InitialViewHttp.cs`, `NativeInitialViewHttpRestoresWithOneLocalObservationAndNoProviderWork` | Configured profile recovery through real native stores/transport: exactly one full local observation, zero schema/online/HTTP/capture/PATCH/selection work while provider is held; exact cached rows and unchanged choices |
| `AgentRouteTests.cs` (10 scenarios) | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.AgentHttp.cs`, `NativeAgentHttpPreservesScopedNativeAuthorityWithoutProviderWrites` | Provider-owned HTTP composition for diagnostics, access, membership, rules/guards, drafts, connections/guards, automatic access and MCP; actual native capture/provider counters |
| `AdoEditorTests.cs` scalar variants (5 cases) | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.ScalarHttp.cs`, `NativeAdoScalarHttpUsesQualifiedNativeAuthority` | One title/HTML/discovered-state PATCH, closed-ticket refusal, relevant/unrelated incomplete policy scope, fresh admission despite cached guidance, scalar withholding and disclosure for owned relationship debt |
| `GitHubEditorTests.cs` `save` and `guards` only (2 cases) | `Twig.Cli.Tests/Commands/GitHubCoreSpineTests.ScalarHttp.cs`, `NativeGitHubScalarHttpUsesQualifiedNativeAuthority` | Full shared provider/locator/field/notice/SDK/CSRF checks; exact Markdown including literal script text, explicit empty clear, HTML/ADO-alias/stale-ticket/actor refusals and actual mutation counts |
| `native-process-browser.mjs` lines 242-282 and 316-318 (2 assertions, 1 case) | `NativePanelTests.ScalarBoundary.cs`, `NativePanelUnpinRetainsOrderedQueryAndOtherPinAndExactTypedStateColors` | Actual DTO-qualified pin PUT/unpin, exact ordered query/other-pin SQLite readback, unchanged three query members, fresh-revision restoration; exact type-qualified state colors from genuine two-type native metadata |
| `PlanTests.cs` (5 variants) | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.PlanHttp.cs`, `NativeAdoCompositionHttpKeepsExactAuthority` | Full native Plan/normal/seed/review/apply composition, actual 36-second metadata delay, navigation-invalidated durable review, committed enable/preparation failure and exact non-preparatory item selection |
| `GitHubEditorTests.cs` `plan` and `apply` (2 variants) | `Twig.Cli.Tests/Commands/GitHubCoreSpineTests.ScalarHttp.cs`, `NativeGitHubScalarHttpUsesQualifiedNativeAuthority` | Shared native guards, isolated Stage and normal Save, retained Plan/scope identities, exact whole review/digest/apply/delivery and no replay |

The original assertions are retained. The Sessions and adapter executable
dispatchers become xUnit cases; each Sessions case owns a fresh directory and
55-second cancellation budget. The Catalog executable is deliberate: one case
starts its own child, commits, exits without a response, and verifies durable
receipt recovery. The projects build with the solution and run in the Windows
and Linux CI matrix under the existing 60-second owned-process wrapper.

Run after building the solution:

```powershell
python eng\run-bounded.py --timeout 60 -- dotnet tests\Twig.Daemon.Catalog.Tests\bin\Debug\net11.0\Twig.Daemon.Catalog.Tests.dll
python eng\verify-trx.py -- dotnet test tests\Twig.Daemon.Tests\Twig.Daemon.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~SessionInvariantTests|FullyQualifiedName~NativeHttpInvariantTests|FullyQualifiedName~NativeSourceAdapterInvariantTests|FullyQualifiedName~NativeAccountLabelInvariantTests" --logger "trx;LogFileName=daemon-invariants.trx" --results-directory artifacts\test-logs\ci\daemon-invariants
python eng\verify-trx.py -- dotnet test tests\Twig.Daemon.Tests\Twig.Daemon.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName=Twig.Daemon.Tests.Native.NativeReadCompositionTests.RedactsDiagnosticsThroughNativeHostAndLoggingFilter|FullyQualifiedName=Twig.Daemon.Tests.Native.NativeReadCompositionTests.RetainsSchemaUntilExplicitNativeRecheck" --logger "trx;LogFileName=read-composition.trx" --results-directory artifacts\test-logs\ci\read-composition
python eng\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority" --logger "trx;LogFileName=presentation-http.trx" --results-directory artifacts\test-logs\ci\presentation-http
python eng\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativeInitialViewHttpRestoresWithOneLocalObservationAndNoProviderWork" --logger "trx;LogFileName=initial-view-http.trx" --results-directory artifacts\test-logs\ci\initial-view-http
python eng\ci-checks.py native-scalar-http
python eng\ci-checks.py native-plan-http
```

The presentation case reuses the existing provider-owned CLI fixture directly,
including its real native SQLite stores and synthetic provider HTTP transport.
The CLI test project references the production daemon, not another test project.
No source links, new friend identities or production hooks are needed. The
original complete-choice comparison uses the public `view-state` JSON response
instead of the daemon's internal serializer; the second view uses the same
explicit store/profile pair. These are runner adaptations, not reduced assertions.

## Diagnostic and schema read composition

The two read-composition cases reuse the original test gateway's compact modes,
not its serving loop, static assets or browser transport. Sources were fetched
directly from Canvas main `f154a3502f2458cd328e51b71802ae108bbfa12d`;
`tests/Bench.Service.Tests/NativePanelTests.cs` is blob
`855acf05e0d94fae6c9345c4f3342bb211e99aa0`, and
`tests/native-diagnostics-browser.mjs` is blob
`a6ebc8294aa294d62ba725920aab2172b17139e2`. The other original sources are
`tests/schema-refresh-browser.mjs` and `tests/native-process-browser.mjs`.

The diagnostic case installs a test logger provider on the actual built host's
`ILoggerFactory`. Production category/level filtering must retain warning
summaries while suppressing informational and unrelated-category controls.
The real adapter retains 111 schema gaps and exactly three items; each ordinary
read mode's first summary has count 113 and no truncation/error. Bench, picker,
drafts, planning read/refresh/catalogue/sources and browse DTOs contain no raw
diagnostic, secret or item-content markers. Changed sets and A-to-B-to-A
reappearance are observable in both ordinary read modes; 1000 gaps produce
count 128 and truncation 874 without changing source identity or item keys.
Every emitted summary is under 300 characters and contains only counts and
an error enum, never a diagnostic payload.

The polling baseline is deliberately taken after each real context has first
been observed: offline Bench and online-validated picker/drafts each emit one
113-count summary, planning emits its 111-gap summary, and explicit browse's
online-metadata projection emits 112. Three mixed Bench/picker/drafts polling
cycles then emit **zero** new summaries. This preserves the original browser's
post-startup baseline invariant, rather than assuming all read modes have
identical annotations or weakening the assertion to a minimum count.

This adoption exposed a latent source-level contradiction, not pinned-provider
drift or evidence of a historical failed run. Canvas's gitlink points at Twig
`3880046cdee92416066cfa1cf31d9fa3fdcce43d`. Its diagnostic and projection
implementations match Twig's pre-correction versions: offline and online reads
have legitimately different mode notes, but shared one latest fingerprint.
Alternating them displaced each other's set. A separate narrow correction
partitions the latest fingerprint by the actual typed observation mode.
It retains both notes, all counts, changed-error reporting and A-to-B-to-A
semantics; it stores no all-seen history. Fixed enum slots bound retention to
64 sources times three areas times four contexts (three modes plus no mode),
while the process-wide 256-entry summary limit remains shared. The focused
`BoundsAndRedactsSourceDiagnostics` regression exercises every slot, repeated
contexts, source-authority conflict/error changes, eviction and the shared cap.
No metadata scheduler or diagnostic protocol is introduced.

The schema case counts actual gateway schema discoveries. Ordinary Bench reads
retain the original observation time, fingerprint and two-type roster even
when the three cached items change from Nebula to Orion types. An explicit
native recheck changes metadata/time while preserving all source/item keys and
the exact Bench definitions, revisions and selectors. Discovered types retain
their SVGs and state fields; a roster genuinely missing the signal type leaves
exactly one unmatched item, then explicit known-roster recovery restores it.

The original browser's failed refresh is a page-route-intercepted 503, not a
provider failure. Here a genuine gateway recheck failure returns 503, preserves
the entire retained schema/observation file byte-for-byte, and leaves pending
durable evidence. An ordinary cached Bench read releases **no items**, even
after the injected failure is removed; only the subsequent successful explicit
recheck in this scenario restores that content. Fresh online access validation
continues to follow the existing production policy; retained bytes alone are
never treated as reauthorization.

## Narrow supported-behavior restorations

The original tests exposed two behaviors present in Canvas's pinned provider
branch but absent from Twig main: `eaab4f7` repository Bench memory and `e67dad2`
attached account display metadata. Their specific implementations are restored
with the original regressions, not a merge of the divergent branch. Remembered
selection remains bound to store and verified repository identity, preserves
session choice/default precedence and never selects a CLI workspace. Account
metadata uses the exact local binding; it does not acquire credentials.

## Scalar-editor HTTP and two residual Panel assertions

The scalar slice fetches the original sources directly from the same Canvas
main above: `AdoEditorTests.cs` blob
`50f1725ea608afb476dc0532851493410df814f7`, `GitHubEditorTests.cs` blob
`a3ca89da602a9e6f13e0362a952482a79c5bb13a`, and the small preconditions in
`tests/Bench.NativeFixtures/NormalSeedFixture.cs` blob
`1617746c46b40e5a4bbbc61cee2ec4821756f9d8`. No complete NormalSeed or
Relationship fixture is copied. Both scalar suites compose the real
`NativePanel`, `NativeSourceAdapter`, `NativeBenchHost` and canonical
`UserBenchStore.ForRegistry`, reusing the existing CLI provider fixtures'
real SQLite stores and synthetic provider HTTP transports. The ADO gateway
is the existing presentation/Agent gateway; GitHub uses its existing native
fixture methods and a small source gateway. There are no production changes,
public wrappers, friend identities, reflection or test-project references.

The original executable selectors map to the five ADO rows as follows:
`--ado-editor-tests` to `save`, `--ado-editor-schema-tests relevant` and
`unrelated` to `policy-relevant` and `policy-unrelated`,
`--cached-editor-guidance-tests` to `cached-guidance`, and
`--relationship-counterfactual` to `relationship-debt`.
`--github-editor-tests save` and `guards` map to their identically named
GitHub rows. The CI group runs each of these seven scalar rows and the one
Panel case in a separate selector-verified 60-second process, each with a
55-second owned cancellation budget and bounded HTTP requests. Every invocation
has one TRX logger; produced TRX files are uploaded even when a later row fails.
Scenario strings do not occur in the method names, so display-name filters
cannot inadvertently select an entire theory.

ADO capability and CSRF refusals precede any provider acquisition or capture.
The successful normal Save verifies all three fields through native cache and
journal readback and dispatches exactly one PATCH; closing a freshly opened
handle makes its subsequent Save conflict without another PATCH. The scoped
policy variants retain `Fixture.Other` and its actual gaps on the wire. An
incomplete relevant dependency endpoint refuses with 503 before any PATCH or
journal attempt; an unrelated incomplete visible type allows one verified
title Save, retaining the full wire schema fingerprint and a nonempty policy
revision. Cached guidance performs zero preliminary online gateway reads but
positive actual metadata and item transport; an explicitly qualified endpoint
whose real revision changes refuses Stage with 409 and no capture/write delta.
Missing cached identity, unreadable remote item and changed actor withhold
forms without switching the selected context. Relationship debt is genuinely
staged locally before HTTP; the scalar form withholds every field, evaluation
returns 422/Unsupported, and the wire explicitly discloses the one retained
intent. This is not adoption of the full relationship route suite.

GitHub shared checks retain its provider, displayed locator, native title/state
fields, Markdown body, read-only collection and both limitation notices.
The first Save changes title, exact literal Markdown and CLOSED state in one
actual updateIssue, with native cache/journal evidence; explicit empty Markdown
clears in a second verified write and leaves no draft. HTML coercion (400), an
ADO alias (422), a closed ticket (409) and a changed actor (403/409) all refuse
with zero provider mutations. SDK denial includes the original evaluate,
stage, review and apply routes without adopting their Plan behavior.

The Panel case reuses the compact three-item/two-custom-type gateway, native
adapter and production HTTP host in `ReadCompositionFixture`. It adds direct
pins from the actual source DTOs alongside the original query, removes only
the first pin, and compares the exact ordered `[original query, other pin]`
definition through GET and the real native store. All three query members
remain projected, then the original definition is restored with a fresh
revision. The added stale PUT is a genuine native 409 preserving the exact
stored definition, not the browser's intercepted canned response. The color
assertion derives expected names/colors by type reference from actual fixture
metadata, first establishing its same-name/distinct-color counterfactual.
It proves projection fidelity, not a general rule requiring different colors
for every pair of native types.

## Plan HTTP composition

The seven Plan cases independently fetch their source of truth from Canvas main
`f154a3502f2458cd328e51b71802ae108bbfa12d`: `PlanTests.cs` blob
`ee4b11096ba08cdbe6709f747e5ed387f6d750d6` and `GitHubEditorTests.cs` blob
`a3ca89da602a9e6f13e0362a952482a79c5bb13a`. Original dispatch was checked in
`tests/Bench.Service.Tests/Program.cs` blob
`41502ce0612003f28de009b0b79ccad29ec3bb30` and `.github/workflows/ci.yml` blob
`640141388829e2b75b91b1949181bf62956e824c`. The five original Plan variants map
to `full`, `delayed`, `navigation`, `preparation`, and `selection`; the last two
are original Program dispatch variants even though the original CI Plan loop
only lists the first three. GitHub retains the original `plan`/`apply` names.
`native-plan-http` invokes seven separate nonoverlapping exact-method/display
selectors, checks exactly one executed result in each TRX, and uploads
`plan-http/*.trx`. Every invocation retains the unchanged 60-second external
owned-process-tree verifier and a private 55-second cancellation budget.
Delivery re-fetch at Canvas main `73d10b2033367e3c90780f5c8464993c7830c8bd`
confirmed the complete Plan, GitHub editor and Program source blobs unchanged.

The real ADO and GitHub native fixtures, canonical `ForRegistry` user store,
native panel/adapter, production host and ephemeral owner-connected loopback
listeners are reused. The historical standalone `benches.db` is deliberately
not used: mode, native editor and HTTP must share one canonical owner store.
Existing `NativeItemEditor.OpenAsync(..., prepareScope: false)` replaces the
absent old fixture wrapper. Test gateways count actual preparation/editor and
review calls; they delegate native operations, never return canned Plan data.
All callbacks are cleared, held metadata released/cancelled, owner sessions
revoked and listeners stopped in `finally`. No new production API, friend,
reflection, source link, assembly identity or test-to-test reference is added.

ADO explicit enable prepares exactly one current-source/root scope in a
separate Plan store. Each subsequent item selection leaves the exact
Plan/Bench revision and source tuple unchanged and never prepares again.
SDK review/seed/stage/apply refusals have no HTTP/capture delta; old normal
tickets are fenced. Full flow stages exact titles for items 42/43 with zero
writes and exact two-row draft overlays. A genuine public
`NativePanel.OpenAsync` context and `ReadViewAsync` typed composition retain
their enabled/draft labels across a later actual mode transition; this is not
a JSON clone compared with itself. Independent normal Save publishes only
description (one PATCH and original native cache/journal evidence), then both
Plan titles remain. The intrinsic owned Plan seed is staged with discovered
type/state, has no invented URL, and makes three overlay members/effects.
Wrong digest dispatches nothing. Exact apply verifies three operations and
delivers one scope: total three PATCHes (including normal Save), one synthetic
create, four committed synthetic mutations. Native SQLite binds the owned seed
UUID to its remote mapping, retains both Applied and Verified receipts per
operation, settles captures and additively pins/caches delivery. Repeated HTTP
Apply is 409 with unchanged counts and all three final members are remote.
This intrinsic Plan seed does not adopt or expand the separate NormalSeed suite.

The delayed case stages exactly one actual isolated native draft via the
existing fixture API, then delays exactly one real project-metadata callback
by 36000ms. Review POST returns routing in under two seconds; the original
operation is observed Reviewing without a review and eventually Reviewed
beyond 36 seconds, with one gateway Review, no replay and no provider mutation.
The navigation case first acquires a genuine reviewed grant, then starts a
second Review and holds its actual metadata boundary while closing the real
HTTP editor. The first grant's exact ticket/digest refuses while blocked,
after close and after release (two intended Reviews, no replay); the same
operation ends with `PlanNavigationChanged` and no review, while native
`LastReviewDigest` survives. Observing that exact durable review produces a
real observation-only ticket (`canApply: false`); even that exact ticket/digest
cannot dispatch a non-delivery Apply (`409 PlanAlreadyStarted`). This maps the
original private `Plan.Ticket`/`ApplyAllowed` safety assertion to meaningful
existing public authority boundaries. Literal private empty-ticket equality
is not inspected: Review itself already clears that ticket before metadata,
so its early equality alone does not distinguish editor close. No private-state
gap is hidden as a literal port. Preparation failure throws a real metadata
`IOException`: 503 explicitly says mode was enabled and durable/public mode
remains enabled without rollback or writes. Enable-only performs both guarded
selections without staging or provider writes.

GitHub shares its original provider/locator/native fields, SDK/CSRF, Markdown,
read-only collection and non-atomic/undiscoverable-automation notices. Stage
captures three scalar fields locally, blocks normal Save and reviews exactly
one effect without writes. Plan-only verifies an independent body-only normal
Save (original title/state unchanged), then the same exact Plan/scope identity
and staged title return with exactly one updateIssue total. Apply refuses the
wrong digest without dispatch, completes the exact one-operation/one-scope
review with one updateIssue, settles captures, and delivers exact title,
Markdown and CLOSED state without drafts to the original native cache and
synthetic remote. Repeated Apply remains 409 with one write. Creates/comments
remain zero. No TLS/Negotiate/Windows-owner or live provider claim is made.

## Not adopted or qualified here

This is not permission to delete the whole Canvas native integration lane:

- `NativePanelTests.RunWindowsAuthentication` and `NativeProgramProbe.Run`:
  actual trusted HTTPS, Windows Negotiate, installed daemon/asset selection and
  retained-session qualification remain in Canvas, explicitly **unexecuted here**.
- NativePanel `--serve-*` branches, assembled `wwwroot`/vendor-asset checks and
  cross-site `/canvas` shell assertion remain consumer/browser concerns. Their
  serving loops and scroll fixture are not copied. Only the reusable compact
  diagnostic/process gateway modes are restored; default native HTTP assertions
  are unchanged. Diagnostic DOM/geometry/three-visible-row assertions, process
  icon rendering/one-unknown warning, filtering/layout and browser request
  enforcement remain consumer-owned. Original consumer tests remain retained
  until an independent comparison of the committed provider gates authorizes
  their removal; these two cases do not dispose of the whole native suite.
- The scalar and Plan cases above do not adopt the `PlanTests` serving loop or
  external-edit stdin commands, `NormalSeedTests`, `RelationshipTests`,
  `GitHubLocalRelationshipTests`, `EditorChoiceFixture` or their remaining
  `Bench.NativeFixtures/*.cs` dependencies. These sources remain upstream;
  neither whole editor file is declared deletable here. Existing CLI/Core
  family coverage is not HTTP composition equivalence. Removal remains gated
  on the consumer peer's independent comparison of committed provider gates.
- All other process-browser geometry, icon rendering, layout, filtering,
  storage, marquee, clipboard and intercepted-response assertions remain
  consumer-owned. Only the two precise Panel assertions above move here.
- Source/artifact stamp suites are source-pairing checks, not adopted provider
  behavior.
- Startup and warm-first-paint browser timing/DOM assertions stay consumer-owned.
  The native initial-view HTTP case proves acquisition counts, not a browser's
  under-1000ms startup or under-250ms warm paint with a delayed provider.
- Canvas's pinned `e65c659` periodic metadata lifecycle is not restored here:
  all nine cases for the 15-minute admission-bound discovery/retry schedule, startup sweep,
  failure continuation and durable denial/cancellation fencing remain a
  separate existing-behavior disposition. Ordinary-read cache retention and
  explicit recheck tests do not establish that background lifecycle.

All executed HTTP tests use owned ephemeral loopback listeners and synthetic
provider/owner seams, or the real reader's uninitialized-directory refusal.
They do not establish positive TLS/Windows-owner or live provider qualification.
There are no certificate trust changes, shared installs, Startup changes or real
provider writes.
