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
- `AgentRouteTests` scenarios (`diagnostics`, `access`, `membership`, `rules`,
  `rule-guards`, `draft`, `connections`, `connection-guards`, `owner-automatic`,
  `mcp`) remain coupled to the ADO editor fixture. Their HTTP composition and
  real native capture/provider-call assertions are **not replaced** by either
  the synthetic gateway suites or the distinct presentation HTTP case.
- `AdoEditorTests`, `EditorChoiceFixture`, `GitHubEditorTests`,
  `GitHubLocalRelationshipTests`, `NormalSeedTests`, `PlanTests`,
  `RelationshipTests` and `Bench.NativeFixtures/*.cs` remain upstream.
  Existing CLI/Core coverage does not by itself establish equivalence for their
  HTTP/browser composition, ticket/digest/journal/uncertain-delivery assertions.
- Source/artifact stamp suites are source-pairing checks, not adopted provider
  behavior.
- Startup and warm-first-paint browser timing/DOM assertions stay consumer-owned.
  The native initial-view HTTP case proves acquisition counts, not a browser's
  under-1000ms startup or under-250ms warm paint with a delayed provider.
- Canvas's pinned `e65c659` periodic metadata lifecycle is not restored here:
  the 15-minute admission-bound discovery/retry schedule, startup sweep,
  failure continuation and durable denial/cancellation fencing remain a
  separate existing-behavior disposition. Ordinary-read cache retention and
  explicit recheck tests do not establish that background lifecycle.

All executed HTTP tests use owned ephemeral loopback listeners and synthetic
provider/owner seams, or the real reader's uninitialized-directory refusal.
They do not establish positive TLS/Windows-owner or live provider qualification.
There are no certificate trust changes, shared installs, Startup changes or real
provider writes.
