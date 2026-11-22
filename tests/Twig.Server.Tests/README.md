# Provider-owned server invariant tests

These tests adopt backend assertions from `PolyphonyRequiem/twig-bench-canvas`
at `f154a3502f2458cd328e51b71802ae108bbfa12d`, without a Canvas checkout,
presentation assets, browser transport, or the historical test assembly identity.
Existing `Bench.Service.Tests` friend access remains during the consumer cutover;
the new tests use `Twig.Server.Tests`. No production API was made public for tests.

## Runnable ownership

| Original Canvas source | Twig source / selector | Retained assertions |
| --- | --- | --- |
| `tests/Bench.Catalog.Tests/Program.cs` | `tests/Twig.Server.Catalog.Tests/Program.cs` (20 console cases) | Identity replacement, canonical uniqueness and concurrent CAS, receipt replay across reopen and commit-without-response process exit, typed selectors, source-qualified projection, unknown membership, drafts/mappings/tombstones, admission and offline guards |
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
| `NormalSeedTests.cs` (all 7 variants) | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.SeedHttp.cs`, `NativeAdoSeedHttpKeepsExactAuthority` | Actual owned seed projection, empty-field authoring, exact UUID/source/owner/Plan isolation, native create/receipts/cache/pin delivery, lost-response protection, original-reference continuation without replay, SDK/owner/CSRF/ticket/definition/actor guards |
| `EditorChoiceFixture.cs` Core evaluator primitives only | `Twig.Core.Tests/CompoundLinkContractTests.cs`, `EvaluatedEditorOverlayReplacesConditionalChoicesAndStatesAndRejectsObsoleteValues` and `EvaluatedEditorOverlayWithoutMatchingAllowedRuleRemovesTheConstraint` | Conditional choice/state replacement, obsolete-value errors and actual effect refusal until both values are corrected; no matching Allowed rule is unconstrained, not constrained-empty |
| `RelationshipTests.cs` (6 HTTP scenarios) | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.RelationshipHttp.cs`, `NativeAdoLinkHttpKeepsExactAuthority` | Qualified native pair evaluation, local staging/reopen, atomic add/remove, strict input/SDK/CSRF/ticket guards and protected lost acknowledgement with exact capture/plan/journal retention |
| `GitHubLocalRelationshipTests.cs` (5 HTTP feature scenarios and 9 binding scenarios) | `Twig.Cli.Tests/Commands/GitHubCoreSpineTests.RelationshipHttp.cs`, `NativeGitHubLinkHttpKeepsExactAuthority` | Real two-issue LOCAL qualification, nonpublishing review/CAS/tombstone lifecycle, all local-route guards, original Applying protection, actual SQLite Applied receipt continuation, and changed-binding refusals |

The original assertions are retained. The Sessions and adapter executable
dispatchers become xUnit cases; each Sessions case owns a fresh directory and
55-second cancellation budget. The Catalog executable is deliberate: one case
starts its own child, commits, exits without a response, and verifies durable
receipt recovery. The projects build with the solution and run in the Windows
and Linux CI matrix with a 60-second process timing warning and a separate
300-second owned-tree safety timeout. The solution build's timing target is
120 seconds, also with a 300-second safety timeout. Slow completion warns without
changing the child exit code; safety cancellation still fails and reaps the
owned tree. TRX checks still require every selected result to pass with no skips.
Behavioral HTTP assertions and causal/liveness checks remain correctness guards;
the adopted HTTP timing policy below separates their generic elapsed targets
from case runaway cancellation.
The offline smoke supervisor uses the same warning/safety split; its command
outcomes, no-network isolation, trace and two-second graceful server shutdown
check remain unchanged.

Run after building the solution:

```powershell
python eng\run-bounded.py --warn-after 60 --timeout 300 -- dotnet tests\Twig.Server.Catalog.Tests\bin\Debug\net11.0\Twig.Server.Catalog.Tests.dll
python eng\verify-trx.py -- dotnet test tests\Twig.Server.Tests\Twig.Server.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~SessionInvariantTests|FullyQualifiedName~NativeHttpInvariantTests|FullyQualifiedName~NativeSourceAdapterInvariantTests|FullyQualifiedName~NativeAccountLabelInvariantTests|FullyQualifiedName~CatalogChangesTests" --logger "trx;LogFileName=server-invariants.trx" --results-directory artifacts\test-logs\ci\server-invariants
python eng\verify-trx.py -- dotnet test tests\Twig.Server.Tests\Twig.Server.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName=Twig.Server.Tests.Native.NativeReadCompositionTests.RedactsDiagnosticsThroughNativeHostAndLoggingFilter|FullyQualifiedName=Twig.Server.Tests.Native.NativeReadCompositionTests.RetainsSchemaUntilExplicitNativeRecheck" --logger "trx;LogFileName=read-composition.trx" --results-directory artifacts\test-logs\ci\read-composition
python eng\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority" --logger "trx;LogFileName=presentation-http.trx" --results-directory artifacts\test-logs\ci\presentation-http
python eng\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativeInitialViewHttpRestoresWithOneLocalObservationAndNoProviderWork" --logger "trx;LogFileName=initial-view-http.trx" --results-directory artifacts\test-logs\ci\initial-view-http
python eng\ci-checks.py native-scalar-http
python eng\ci-checks.py native-plan-http
python eng\ci-checks.py native-seed-http
python eng\ci-checks.py native-relationship-http
```

The presentation case reuses the existing provider-owned CLI fixture directly,
including its real native SQLite stores and synthetic provider HTTP transport.
The CLI test project references the production server, not another test project.
No source links, new friend identities or production hooks are needed. The
original complete-choice comparison uses the public `view-state` JSON response
instead of the server's internal serializer; the second view uses the same
explicit store/profile pair. These are runner adaptations, not reduced assertions.

The retained GitHub root phase (`python eng\ci-checks.py github-root`) follows
the portable connection contract: its init group runs six rows, including two
distinct missing-registration/credential refusals (exit 1) and one malformed
explicit-account refusal (exit 2). Each refusal checks the structured cause and
zero credential/HTTP calls, workspace creation, database or SQLite sidecars.
An omitted `--account` is not a usage error when a matching local binding exists.

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

Current-main advisory queue overflow uses an all-resource `Resync` signal instead
of retaining the original literal content revision. Both content/presentation
queue orders assert this stronger invalidation: `Resync` is true, the signal is
neither presentation-only nor agent-scoped, and exactly one signal remains
buffered. The standalone overflow theory covers both orders as well. This
preserves content invalidation without narrowing the current multi-resource
notification contract.

The existing host-utility and fresh-init fixtures explicitly register their
provider connection and named credential before exercising implicit registered
connection selection. They do not rely on retired `--default` flags or legacy
default-binding fallback; explicit-binding initialization remains a separate case.

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
Panel case in a separate selector-verified process. The initial adoption used
a 60-second process bound and 55-second case cancellation; the timing-policy
revision below separates warning targets from runaway protection. Every invocation
has one TRX logger; produced TRX files are uploaded even when a later row fails.
Scenario strings do not occur in the method names, so display-name filters
cannot inadvertently select an entire theory.

Scalar cases emit monotonic fixture, listener and HTTP request/response phase
timings with actual provider-request and write counters into xUnit output.
These diagnostic lines contain no request bodies or capabilities, do not catch
timeouts, and do not change the assertions. The original diagnostic-only change
did not change request/case/process deadlines.
A passing diagnostic run does not supersede a failed qualification gate.

### Adopted HTTP timing policy

The scalar/Panel, Plan, seed and relationship adoption keeps the original
55-second whole-case and 10-second scalar/Plan/seed/Panel request timings
(55 seconds for relationship requests) as **warning targets**, not functional
assertions. Exceeded targets emit `TIMING WARNING` with elapsed time through
xUnit output. The generic case token instead cancels after 300 seconds and
still reaches every HTTP send/body read and existing cancellation-aware
operation. The HTTP client adds no shorter independent cancellation.
The Panel case and its two read-composition controls share this private fixture
policy and output plumbing. No production timeout is changed.

The integrated external verifier warns after 60 seconds and retains a separate
300-second owned-tree safety timeout, with strict actual-TRX validation.
A canceled or unexecuted case never counts as passed.
Status, authority, provider/write counts, ordering and evidence assertions are
unchanged. In particular, Plan's held-provider response/liveness assertions,
five-second callback-entry waits, injected 36-second delay and minimum elapsed
assertion remain behavioral checks, not generic performance warnings.

Before this policy revision, exact carrier `c47880da` completed Linux 57/57
relevant TRX rows. Windows produced 55 passed rows, one elapsed-canceled
`github-binding-denied` row and one unrun `github-binding-generation` row
(including the independent controls in those totals). Cancellation occurred
during the initial LOCAL review, before denied-binding mutation/assertions;
it demonstrates incomplete execution, not a failed security assertion.
The successful cold Windows solution build took 132.35 seconds with zero
warnings/errors: correctness passed, elapsed build time is a warning.
Historical failure evidence is retained, not relabeled as passing. Revised
source requires its own qualification; these results belong to `c47880da`.

The timing-policy revision subsequently passed its focused Windows
qualification: denied-binding and generation-binding each ran once in isolated
processes, followed by the Panel case and two shared read-composition controls.
The existing strict TRX verifier checked actual 1/1/3 passed rows, all selectors
covered and zero skips. Denied/generation took 22.154/22.101 seconds, each with
98 synthetic provider requests, zero provider writes, one capture/plan/
journal and zero receipts. Panel/schema/diagnostics took 2.833/2.958/1.460
seconds. The remaining already-passed carrier rows were not duplicated or
claimed as new-tip execution. No old latency cause or performance fix is
inferred from these faster runs.

Both affected test projects built successfully without restoring dependencies:
CLI 87.63 seconds and Server.Tests 9.39 seconds, zero errors. Each build reported
four existing MSBuild Git/SourceLink warnings from Infrastructure/RenderTree
("Repository has no commit" and empty source-link information), not suppressed.
Focused artifacts are under `artifacts/test-logs/ci/timing-warnings-candidate/`;
the earlier canceled TRX remains separate under `windows-c47880da/`.
No owned test/build/MSBuild-node process remained after qualification.

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
`plan-http/*.trx`. The original invocation used a 60-second external verifier
and 55-second case cancellation; the adopted HTTP timing policy above now
separates elapsed warnings from the 300-second case runaway cancellation.
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
The navigation case holds an explicit Apply at its metadata boundary while
closing the HTTP editor. Concurrent Apply is busy; the original operation ends
with `PlanNavigationChanged`, no admitted attempt, and no provider writes.
The full case stages more intent after reading Plan status, proves the old
staged version cannot dispatch, and applies the new version without preview.
Preparation failure throws a real metadata
`IOException`: 503 explicitly says mode was enabled and durable/public mode
remains enabled without rollback or writes. Enable-only performs both guarded
selections without staging or provider writes.

GitHub shares its original provider/locator/native fields, SDK/CSRF, Markdown,
read-only collection and non-atomic/undiscoverable-automation notices. Stage
captures three scalar fields locally, blocks normal Save and reviews exactly
one effect without writes. Plan-only verifies an independent body-only normal
Save (original title/state unchanged), then the same exact Plan/scope identity
and staged title return with exactly one updateIssue total. Optional review
retains no attempt. Explicit Apply completes the staged one-operation/one-scope
Plan with one updateIssue, settles captures, and delivers exact title,
Markdown and CLOSED state without drafts to the original native cache and
synthetic remote. A duplicate staged version cannot dispatch again. Creates/comments
remain zero. No TLS/Negotiate/Windows-owner or live provider claim is made.

## Normal-seed HTTP and two Core authoring-rule residuals

This slice independently fetched Canvas main
`73d10b2033367e3c90780f5c8464993c7830c8bd` (the initial adoption source was
`f154a3502f2458cd328e51b71802ae108bbfa12d`). Exact source identities:
`tests/Bench.Service.Tests/NormalSeedTests.cs` blob
`6c46022a2061f910ef68e0151ea057faef5ca5ea`,
`tests/Bench.NativeFixtures/NormalSeedFixture.cs` blob
`1617746c46b40e5a4bbbc61cee2ec4821756f9d8`,
`tests/Bench.Service.Tests/EditorChoiceFixture.cs` blob
`de2a4745618fddc48d8295268c3156154c2e1d5f`.
The original Program blob `41502ce0612003f28de009b0b79ccad29ec3bb30`
dispatches `--normal-seed-tests <scenario>`; CI blob
`640141388829e2b75b91b1949181bf62956e824c` explicitly runs
`projection`, `lifecycle`, `pin`, `origin`, `unknown`, `guards`, `ownership`
under its 60-second owned-tree wrapper. These are all seven native cases,
not the separate browser loop (`lifecycle`, `pin`, `unknown`, `authority`).

`native-seed-http` retains those seven exact strings, using the full method
`Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoSeedHttpKeepsExactAuthority`
and `&DisplayName~<scenario>`. No scenario string occurs in the method name.
Each invocation passes through `eng/verify-trx.py` (completed/passed-only counters
and zero skips), followed by an
exactly-one-result check. CI uploads each `seed-http/seed-<scenario>.trx`,
including earlier results if a later case fails. Each case retains 55-second
case and 10-second request warning targets with 300-second case cancellation,
a disposable fixture home,
ephemeral loopback listener and explicitly revoked owner/stopped host in
`finally`. No shared helper or production code changes are required.

| Original scenario/assertion | Provider-native proof and adaptation |
| --- | --- |
| `projection` | Stage a real owned native seed first; ready capture, UUIDv7 and null baseline; existing `/editor` opens the same qualified ID; `/seeds` projects temporary membership and the same UUID/source; permanent Bench revision/connections/selectors unchanged; zero creates/PATCHes/journal |
| `lifecycle` | Discover exact connected type/state; all creation fields Null; incomplete Save is 400; Stage/reopen keeps UUID and capture; one actual synthetic ADO POST/commit, no PATCH; canonical SQLite plan/create operation/digest, mapping and Applied plus Verified receipts; settled capture and permanent source-qualified remote pin; exact cached title and HTTP member, no stale local row |
| `pin` | Real SQLite pin-update failure after actual remote commit yields VerifiedDeliveryPending; Core draft is gone, original capture remains; restore, add unrelated uncached pin 44, enumerate/observe original reference, wrong digest 409, continue twice; same plan/attempt/receipts/mapping and one POST; both pins survive; uncached 44 explicitly makes HTTP projection Unavailable, never a false empty-success or failed delivery |
| `origin` | Real synthetic provider current-item read failure only after two verification readbacks; verified journal but pending delivery/no pin; restore and enumerate the original capture without a Core draft, observe/wrong-digest/continue twice; exact native cache/pin delivery and no new create/journal/receipt |
| `unknown` | Actual synthetic remote commit followed by lost response; Indeterminate journal with original receipt, no mapping/pin, original UUID draft and unsettled capture retained; enumerate/observe original reference, wrong digest refused, repeated delivery stays Protected; receipt content/attempt/plan/UUID unchanged and no replay |
| `guards` (shared preconditions also run in all non-projection cases) | SDK cannot enumerate/create/observe/deliver; invalid owner capability and missing CSRF refuse before any provider HTTP/capture; invalid connected type is 400; unselected source is 409 and retires old ticket; real Bench definition revision advance rejects the next ticket; changed actual provider principal rejects Save and withholds seed content; zero provider mutations and no settled capture |
| `ownership` | Real CLI draft, different-Bench seed and isolated-Plan seed have distinct keys; exact original capture binds user instance, Bench, root, source/store and normal scope; only the normal seed is owned/member; both visible unowned drafts are HTTP NotFound for edit and nonmembers, Plan seed stays outside the normal store and is Unsupported through native selection; normal publication preserves foreign debt and Plan owner |

The original fixture's `SeedCreateAttempts` is mapped directly to the existing
synthetic provider HTTP handler's `_transport.PostCount`, not a gateway claim.
`FailSeedDelivery`/restore reuse the existing private transport callbacks and
`SeedPinFailureAsync`; no public test hooks are added. Foreign-debt setup uses
the existing actual CLI workspace/native editor/Plan store APIs in the same
partial class. All native enumeration/editor/HTTP paths use canonical
`UserBenchStore.ForRegistry`, not the original standalone store constructor.
An empty Bench retains its explicit connection while clearing selectors:
current native Bench source selection is connection-owned. Its initial HTTP
rows are empty while the native item cache still contains both original items.
This is an explicit fixture/API adaptation, not treating an unselected source
as an empty source or weakening owned-seed membership.

Existing direct native tests in `NativePublicationConsumerTests.NormalSeeds.cs`
already own lifecycle, verified origin/pin continuation, unknown creation,
CLI/other-Bench/Plan isolation, actor/mode/schema/Bench/ticket guards and
creation-requiredness. They are mapped, not copied into new contract wrappers.
The new seven cases supply the missing actual production HTTP composition and
canonical capture/journal assertions. `NativeHttpGateway` is reused unchanged;
its existing non-preparatory `NativeItemEditor.OpenAsync(..., prepareScope: false)`
replaces the removed consumer `OpenExistingHumanHostForPanelAsync` wrapper.

Two Core Facts fill only the missing primitives behind `EditorChoiceFixture`
lines 39-49 and 73-79. Title overlays replace severity A/obsolete with B and
state Start with Finish. Current obsolete values remain invalid; correcting
only one leaves errors, while Finish/B removes them. Actual
`Core.EvaluateEditorFields` errors are compared with `Core.ValidateEffect`
refusals for the retained plan/journal; no synthetic fixture Save is used.
The second Fact changes a matching condition to no matching Allowed rule and
asserts `HasAllowedConstraint: false`, an empty suggestion list and a genuinely
valid unlisted value. Existing
`SupplierProjectionPreservesActualReadOnlyRequiredAmbiguityAndConstraintSemantics`
(`constrained-empty`) already proves true plus empty; existing
`EvaluatedEditorFieldsReuseConditionalRequiredAllowedAndReadOnlyRules` covers
required/read-only primitives, so neither is duplicated.
The unchanged CI Core selector `FullyQualifiedName~Twig.Core.Tests` includes
both new Facts and preserves its `core/core.trx` upload. Focused local evidence
also includes both existing comparator cases in `seed-http/core-authoring.trx`.
DOM current-value retention, select/input switching, focus and all choices
fixture HTTP/browser transport remain consumer-owned. These Core Facts do not
claim native conditional-metadata Save HTTP or browser equivalence.

The per-case `SEED HTTP` TRX output records actual synthetic provider HTTP,
connectionData admission probes, POST/PATCH attempts, committed writes and
readbacks; canonical SQLite captures, journal rows and receipts; and separate
gateway prepared/existing-editor counts. Projection/guards have zero mutations
and journal rows; the other five cases each have one actual create/commit and
zero PATCHes. Unknown has one Indeterminate receipt; verified cases have two
separate Applied/Verified receipts. No real external provider writes occur.
Observed local Windows counters (HTTP totals describe this fixture run, not a
new production request-count contract):

| Case | Provider HTTP | Admission probes | Captures | Create / PATCH / committed | Readbacks | Journal / receipts | Prepared / existing editor |
| --- | ---: | ---: | ---: | --- | ---: | --- | --- |
| projection | 89 | 17 | 1 | 0 / 0 / 0 | 0 | 0 / 0 | 1 / 1 |
| lifecycle | 201 | 31 | 1 | 1 / 0 / 1 | 3 | 1 / 2 | 4 / 1 |
| pin | 288 | 46 | 1 | 1 / 0 / 1 | 4 | 1 / 2 | 10 / 1 |
| origin | 276 | 44 | 1 | 1 / 0 / 1 | 3 | 1 / 2 | 9 / 1 |
| unknown | 242 | 41 | 1 | 1 / 0 / 1 | 0 | 1 / 1 | 9 / 1 |
| guards | 143 | 29 | 1 | 0 / 0 / 0 | 0 | 0 / 0 | 4 / 3 |
| ownership | 273 | 43 | 3 | 1 / 0 / 1 | 3 | 1 / 2 | 4 / 3 |

No native assertion from the original seven-scenario suite is intentionally
unported. Its serve/stdin/static-asset branches are consumer protocols, not
native invariants, and remain unported along with browser interaction/geometry.

## Native relationship HTTP ownership

Source was fetched independently from `PolyphonyRequiem/twig-bench-canvas` main
`73d10b2033367e3c90780f5c8464993c7830c8bd` (original reference
`f154a3502f2458cd328e51b71802ae108bbfa12d`). Exact source blobs:

| Source | Blob |
| --- | --- |
| `tests/Bench.Service.Tests/RelationshipTests.cs` | `b22838d9fd7f3b5e46c3f584123d93f85b771d20` |
| `tests/Bench.Service.Tests/GitHubLocalRelationshipTests.cs` | `2a4abd2ae29a4758d9330e58538c674a63170a64` |
| `tests/Bench.NativeFixtures/RelationshipHostFixture.cs` | `00694c774213a48733c2dbe4dd494697b059e57e` |
| `tests/Bench.NativeFixtures/GitHubLocalHostFixture.cs` | `5d46dbea52102e01c6f9bbe2e51a65c7bce09d0d` |
| `tests/Bench.Service.Tests/Program.cs` | `41502ce0612003f28de009b0b79ccad29ec3bb30` |
| `.github/workflows/ci.yml` | `640141388829e2b75b91b1949181bf62956e824c` |

Program dispatch and both original CI LOCAL lists establish **20 new cases**:
six ADO cases plus five GitHub LOCAL feature cases plus nine `binding-*` cases.
The three `native-*` LOCAL dispatches and eight ADO `RunContract` dispatches
already call provider-owned tests; they are mapped below, not copied or counted
as new HTTP tests.

An additional `observed-removal` ADO row preserves a distinct native invariant
from the original browser fixture, bringing the gate to **21 cases** without
changing the original twenty rows or their budgets. It is not equivalent to
`incoming`, which starts without an edge and performs add then remove (two
effects). The added row starts with a real synthetic provider edge 43 -> 42,
stages incoming removal with empty fields through HTTP, and checks one ready,
unsettled canonical capture, zero-field intent/draft, no plan/journal or
provider write, and the still-observed provider edge. HTTP scalar/pair reopen
retains that original removal and incident evidence; empty-fields Save must
settle the same capture with exactly one revision-guarded target-42 PATCH,
one canonical removal plan/journal, original attempt-bound receipts,
no creates, and both endpoint revisions advanced once.

This additional source-only comparison used Canvas commit
`6841cc9e955d51bbe1b583572303dafda4e89c5a`: `RelationshipTests.cs` blob
`ee1a69a2121e37f89faef030a34881de7acdd693` lines 22-33 and 59;
`RelationshipHostFixture.cs` blob `ec4072f2156c290ad82dc19d3aa2c86672ec94f1`
lines 14-35 and 65-88; `Program.cs` blob
`54d03e671595385cd22b6a98b65fb0a22abae033` lines 75-77;
`tests/relationship-browser.mjs` blob
`78f007dd112f151eac4dbe3e5345d49f9f9991cd` lines 104-120; and CI blob
`cf2fc530ea01f235a97bbf30702abc2d29d0824c` lines 157-161.
The original relationship/helper blobs above already contained this fixture
branch. This is additional browser-server native invariant preservation, not
a retroactive failure of the original finite twenty-case HTTP scope. Browser
DOM, request construction by the UI and Edit-mode readback remain unqualified.
The new row passed its first isolated bounded execution on the integrated
source at `b9740f094e0a681284efd4508549b7954efa92f4` (30.027 seconds,
one passed, zero skipped). Actual synthetic counters were 264 provider HTTP,
one PATCH/committed write, zero creates, one capture/plan/journal and two
receipts. Its exact selector is the same ADO method below with
`&DisplayName~observed-removal`; the label deliberately does not contain
`incoming`, preserving the existing incoming selector's single result.

**Historical qualification was HOLD, not a passing 21-case gate.** After ordinary
integration of scalar carrier `2cef37873dc3ecb8b9279ec773f9765bcb56af2b`,
the bounded CLI build passed with zero warnings/errors. The complete gate
then passed association (45.956 seconds), but outgoing cancelled at its
unchanged 55-second case deadline during the final removal Save. The external
60-second wrapper returned 124 with unconfirmed cleanup (root 142196 already
absent); the remaining nineteen gate rows did not execute. Fresh process
inspection found no root/descendant chain, exact-worktree test command or
active dotnet test/vstest/testhost process before further execution.

One authorized phase-only outgoing reproduction passed in 44.978 seconds:
fixture ready at 5.780 seconds, first Save completed at 29.196 seconds, and
final removal Save ran from 38.293 to 44.228 seconds (480 provider HTTP,
two PATCHes/commits, two captures/plans/journal rows, four receipts).
This establishes no causal repair or base/environment attribution and does
not replace the failed complete gate. Temporary timing diagnostics were
removed; no deadlines, guards or assertions were relaxed and no further gate
rerun was performed. Evidence is retained in
`artifacts/test-logs/ci/relationship-requalify/`: `ado-observed-removal.trx`,
`ado-association-gate.trx`, `ado-outgoing-gate-failed.trx`, and
`ado-outgoing-diagnostic.trx`. Historical counters below are separate and do
not supersede that historical hold. Linux qualification had not been executed
at that checkpoint; later exact-carrier results are recorded above.

The new group uses `FullyQualifiedName=Twig.Cli.Tests.Commands.` followed by
the method below and `&DisplayName~<scenario>`. Every invocation must produce
exactly one passed result with zero skips, checked after the
`eng/verify-trx.py` owned-process wrapper. CI uploads all per-case
TRXs from `artifacts/test-logs/ci/relationship-http/`, including on failure.
Both methods now use the 300-second case token for initialization and requests;
55-second case/request timings are warnings under the policy above.
The original request timeout was 60 seconds. An initially added 10-second cap was
removed after it interrupted genuine acquisition/SQLite writer-fence work on
the shared host. There are no timeout catches, retries or accepted partial runs.

| Method | Original scenarios | Retained native assertions |
| --- | --- | --- |
| `NativePublicationConsumerTests.NativeAdoLinkHttpKeepsExactAuthority` | association, outgoing, incoming, hierarchy | Actual source/endpoint projection; prospective Done close gate without state/capture changes; scalar Save refusal of pair ticket; owned local Stage with title and one delta; scalar debt disclosure; original capture on reopen; empty-new-fields Save; one target PATCH per add/remove including incoming direction, real target/endpoint revisions, canonical Batch plan, original digest/journal/receipt and settled capture |
| same | guards, protected | SDK and CSRF gates before provider acquisition; unknown/null/missing input 400; stale endpoint and closed ticket 409; protected committed effect after lost acknowledgement; no restage/replay; exact drafts, canonical plan bytes, all original journal/receipt evidence and capture `SameAs` readback |
| `GitHubCoreSpineTests.NativeGitHubLinkHttpKeepsExactAuthority` | counterfactual, guards | Qualified two-published-issue projection, `localOnly`, exactly one undirected Association and readonly scalar fields; all five LOCAL SDK routes 403, owner CSRF 403, self/unknown endpoint 404, strict null/missing/unknown body 400, combined scalar/ADO refusal; wrong digest/attempt 409, noncanonical ordinal 400, consumed pair/closed ticket 409; unchanged pristine/original evidence |
| same | lifecycle | Close plus HTTP rediscovery of original canonical plan/digest, first local CAS Verified/settled, repeat Apply 409, read-only observation, reverse pair version 1, canonical removal expected version 1, tombstone version 2; no provider mutations |
| same | protected | Genuine endpoint change after durable Applying: original nonempty attempt, no receipts and no LOCAL edge; byte-exact rediscovery; Apply 409; observation preserves original Core/capture/provenance evidence |
| same | delivery | Real SQLite writer fence after local CAS leaves Applied plus `canContinue`/no `canApply`; explicit HTTP rediscovery and Continue verify/settle the same original digest/ordinal/attempt, retain the first receipt byte-for-byte, append only verification; no replacement/replay |
| same | binding-actor, binding-repository, binding-schema, binding-endpoint, binding-mode, binding-store, binding-bench, binding-denied | Actual respective provider/normal-mode/store-version/Bench evidence changes; cannot verify any effect, exact original capture/plan/journal/provenance remains, zero local CAS and zero provider writes |
| same | binding-generation | Public profile retirement adaptation described below; fresh valid owner still cannot apply the exact retired original ticket/capture/digest |

All HTTP paths compose production `NativeBenchHost`, `NativePanel`,
`NativeSourceAdapter`, and canonical `UserBenchStore.ForRegistry`, with the
existing native CLI fixtures' real SQLite and synthetic provider HTTP. ADO
reuses `NativeHttpGateway`; GitHub reuses `GitHubScalarHttpGateway`. The small
private `LocalHttpFixture` reuses `InitializeLocalPair`, `LocalPairOther`,
`JournalBytes`, `ReceiptBytes` and `LocalCaptureProof` from its existing partial
class. No shared fixture, production API, friend identity or test reference
changed. There are no source links, reflection, Canvas assembly identities,
static assets or fabricated compiled-native stamps.

The redundant direct prequalification open was removed: the actual HTTP pair
must prove the same qualification/shape/direction/readonly assertions. HTTP
`/editor/local/reviews` still exercises the production native canonical reader
and compares its returned bytes/digest with the original. Instead of opening a
second native editor solely to repeat that lookup, additional durable readback
compares the real capture `IntentJson` and Core `PlanBytes`. Preservation
checks compare the complete native state and capture `SameAs`; once a capture
exists they also compare actual SQLite capture/history/registry provenance
rows through the existing helper. Pristine state has no capture or initialized
capture-history generation to inspect, and must remain pristine. The SQLite
writer and provider callback are released in `finally`, as are the owner,
listener and fixture-owned stores/homes.

### Public profile-generation adaptation

The original writes server-internal `owned.ProfileGeneration = Guid.NewGuid()`.
That private-field mutation is **not literally ported or claimed**. The public
boundary test instead creates an actual original Planned capture/review,
performs HTTP `DELETE /session` (real durable generation rotation and revoke),
explicitly reconnects the same profile through public `ConnectOwner`, and
restores the same exact source and Bench. A successful authenticated session
GET proves the new owner capability is valid. Applying the exact *old*
ticket/capture/digest/ordinal/attempt must then return **409**, not a stale-token
401/403. Core state, canonical capture/plan/journal and provenance remain
unchanged, with no LOCAL CAS or provider write. This covers real public
profile-retirement authority; it does not isolate a forged in-memory generation
while deliberately holding every other internal field constant.

### Existing native dispatches, not new wrappers

All following methods have the same `Twig.Cli.Tests.Commands.` prefix. Existing
CI selectors are in `eng/ci-checks.py`, `user-benches` for ADO and `github-root`
for GitHub. They remain unchanged.

| Original dispatch | Existing method | Existing CI result label |
| --- | --- | --- |
| ADO parents-and-cycles | `NativePublicationConsumerTests.NativePublicRelationshipsRefuseParentReplacementAndDependencyCycleThroughCore` | `native-public-relationships-policy` |
| ADO ownership | `NativePublicationConsumerTests.NativePublicRelationshipsDoNotAdoptCliDebtPlanSeedsOrAnotherBench` | `native-public-relationships-isolation` |
| ADO store, source, witness | `NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture` with respective `DisplayName` | `native-public-relationships-capture` |
| ADO bench, mode | same method, respective `DisplayName` | `native-public-relationships-authority` |
| ADO incident-debt | same method, `DisplayName~incident-debt` | `native-public-relationships-debt` |
| ADO protected's extra original-receipts postcondition | `NativePublicationConsumerTests.NativePublicRelationshipsOriginalUncertainReceiptIsProtectedWithoutReplay`, `DisplayName~True` | `native-public-relationships-uncertain` (already runs both bool rows) |
| LOCAL native-mapped | `GitHubCoreSpineTests.NativeGitHubLocalReverseOrderRetainsOriginalUuidAndTombstoneAbaCas` | `native-local-uuid-cas` |
| LOCAL native-applied | `GitHubCoreSpineTests.NativeGitHubLocalAppliedReceiptReopensAndVerifiesWithoutReplay` | `native-local-applied-recovery` |
| LOCAL native-reader | `GitHubCoreSpineTests.NativeGitHubLocalReaderIncludesProtectedOriginalAttemptWithoutReplay` | `native-local-read-protected` |

The extra ADO protected `True` postcondition is independently executable as
`FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsOriginalUncertainReceiptIsProtectedWithoutReplay&DisplayName~True`.
It verifies original title debt, exact receipts/plan and reopen/scalar/pair
refusals after a committed uncertain effect, not merely the new HTTP guard.
All twelve mapped original dispatches in the table were also executed
individually with the unchanged bounded verifier, exactly one passed result
each, in `artifacts/test-logs/ci/relationship-existing/`. Four focused shared
composition checks passed individually in `relationship-shared/`: GitHub scalar
`guards` and Plan `apply` from `NativeGitHubScalarHttpUsesQualifiedNativeAuthority`,
ADO seed `guards` from `NativeAdoSeedHttpKeepsExactAuthority`, and Agent
`rule-guards` from `NativeAgentHttpPreservesScopedNativeAuthorityWithoutProviderWrites`.
Together with the twenty new cases this is 36 passed, zero skipped. The CLI
project build used SDK `11.0.100-rc.3.26522.148`, `--no-restore -f net11.0 -m:1
--verbosity quiet`, with zero warnings/errors; restore was performed only after
the initial missing-assets failure. This session resolved `python3` with no
prefix arguments through the Python runtime skill.

The historical complete 20-case execution produced the following actual synthetic
transport/durable counters (not gateway admission counts). GitHub provider
mutation count is **zero in every row**; LOCAL versions are native SQLite CAS,
never publication. ADO creates are zero.

| Case | Provider HTTP | PATCH / committed | LOCAL version | Captures / plans / journal | Receipts |
| --- | ---: | --- | ---: | --- | --- |
| ADO association/outgoing/incoming/hierarchy (each) | 480 | 2 / 2 | - | 2 / 2 / 2 | 4 |
| ADO guards | 90 | 0 / 0 | - | 1 / 0 / 0 | 0 |
| ADO protected | 185 | 1 / 1 | - | 1 / 1 / 1 | 1 (Indeterminate) |
| LOCAL counterfactual | 64 | 0 / 0 | 0 | 0 / 0 / 0 | 0 |
| LOCAL guards | 96 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL lifecycle | 224 | 0 / 0 | 2 | 2 / 2 / 2 | 4 |
| LOCAL protected | 128 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL delivery | 147 | 0 / 0 | 1 | 1 / 1 / 1 | 2 |
| LOCAL binding actor/repository (each) | 97 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL binding schema/store (each) | 99 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL binding endpoint | 106 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL binding mode/bench (each) | 96 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |
| LOCAL binding denied/generation (each) | 98 | 0 / 0 | 0 | 1 / 1 / 1 | 0 |

No native assertion in the six original ADO or fourteen LOCAL HTTP scenarios
is intentionally omitted other than the explicitly replaced private-generation
mutation above. Serve/stdin/iframe/browser/compiled-artifact qualification is
not adopted. All consumer source deletion remains gated on independent
comparison of the committed provider gates; this PR deletes no consumer source.
## Sessions canonical-root adoption delta

The additional assertions are independently fetched from Canvas commit
`73d10b2033367e3c90780f5c8464993c7830c8bd`, complete Sessions Program blob
`98d716ac80dcf363a2de3c1a7ee02c31d06fa94e` (60,974 bytes). Its lines
130-138 map to the linked-owner `ReadState` canonical-root and real production
HTTP POST binding-read assertions in `SessionTests.RepositorySelection`.
Lines 163-185 map to explicit linked repository-context selection and the
independent repository/worktree/nested-directory fixture with spaces, including
direct store and initial-owner canonical roots and fallback Bench. The existing
lines 186-187 zero-Gateway-read/selection guard still covers every operation.
These 32 added source lines were not part of the original adoption above.

The self-contained production behavior is restored from Canvas's pinned Twig
dependency `8a1b8a62547583fd419677a493f4f6df36cb9995`: verify Git's main checkout
using NUL-delimited worktree porcelain, return its root alongside the unchanged
repository identity, and retain that root in runtime and explicit contexts.
The existing Infrastructure public API catalog records the DTO shape; the POST
body is registered in the existing source-generated transport JSON context.
There are no migration, identity redesign, friend-access or public test hooks.

Unlike the pin, this delta leaves the existing GET route registered. Its
query-string form already returns `403 InvalidTransport` under the global
no-query rule; the added regression preserves that refusal, rather than opening
a new query transport. The added POST performs the same authorized repository
read using bounded JSON and retains SDK-presentation and missing-CSRF refusals.
The fixture Git helper kills its owned process tree on cancellation.

Executed on Windows with SDK `11.0.100-rc.3.26522.148`, warnings-as-errors
unchanged, and resolved Python invocation `python3`:

```powershell
dotnet build tests\Twig.Server.Tests\Twig.Server.Tests.csproj --no-restore -f net11.0 "-m:1" --verbosity quiet
python3 eng\verify-trx.py -- dotnet test tests\Twig.Server.Tests\Twig.Server.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~SessionInvariantTests" --logger "trx;LogFileName=sessions-canonical-root.trx" --results-directory artifacts\test-logs\sessions-canonical-root
python3 eng\verify-trx.py -- dotnet test tests\Twig.Server.Tests\Twig.Server.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativeHttpInvariantTests" --logger "trx;LogFileName=sessions-native-http.trx" --results-directory artifacts\test-logs\sessions-native-http
dotnet build tests\Twig.Infrastructure.Tests\Twig.Infrastructure.Tests.csproj --no-restore -f net11.0 "-m:1" --verbosity quiet
python3 eng\verify-trx.py -- dotnet test tests\Twig.Infrastructure.Tests\Twig.Infrastructure.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName=Twig.Infrastructure.Tests.Services.UserBenchStoreTests.RepositoryBindingsAreUserLocalReusableAndIndependentOfSavedBenchIntent" --logger "trx;LogFileName=repository-bindings.trx" --results-directory artifacts\test-logs\repository-bindings
```

Missing assets (`NETSDK1004`) required a targeted `dotnet restore <project>
--verbosity quiet` for each of those two projects before rebuilding. Both builds
passed; existing MSBuild Git/SourceLink warnings reported "Repository has no
commit" and empty source-link information (not suppressed). The three isolated
TRX runs passed 6, 9 and 1 cases respectively, with zero skips and every selector
covered, through the then-current 60-second owned-tree wrapper.

The exact four-selector CI command shown above was also executed with `python3`:
37 passed, zero skipped, every selector covered, at
`artifacts\test-logs\ci\server-invariants\server-invariants.trx`. The existing
Windows/Linux `server_invariants` step and `twig-server-invariants-${rid}` TRX
artifact already include all six Sessions cases, so no duplicate CI job or
selector was added. Canvas's original CI runs the Sessions executable as a whole
with a 60-second external bound and 55-second internal cancellation; this
historical execution retained those bounds per xUnit case. Current generic
process timing targets warn, as described above; the internal 55-second
cancellation remains unchanged. Only Windows execution is claimed
here, not live-provider, trusted HTTPS or browser qualification.

## Not adopted or qualified here

This is not permission to delete the whole Canvas native integration lane:

- `NativePanelTests.RunWindowsAuthentication` and `NativeProgramProbe.Run`:
  actual trusted HTTPS, Windows Negotiate, installed server/asset selection and
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
- The scalar, Plan, normal-seed and relationship cases above do not adopt the
  `PlanTests`, `NormalSeedTests`, `RelationshipTests` or
  `GitHubLocalRelationshipTests` serving loops or external-edit stdin commands,
  the remaining `EditorChoiceFixture` HTTP/browser
  behavior or their remaining
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
