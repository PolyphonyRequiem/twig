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
| `McpBridgeTests.cs` | `NativeHttpInvariantTests.ScopesMcpBridgeAuthorityAndReleasesItsView` | Exact owner bootstrap, view-scoped forwarding, protocol-only stdout, notification suppression and EOF cleanup |
| `PresentationRouteTests.cs` | `Twig.Cli.Tests/Commands/NativePublicationConsumerTests.PresentationHttp.cs`, `NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority` | Real native transport/capture counters; held-provider response under two seconds; complete persisted choices; genuine cross-view CAS/reconciliation; SDK attachment/layout authority; zero publication/selection writes; content-priority invalidation in both queue orders |

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
python eng\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority" --logger "trx;LogFileName=presentation-http.trx" --results-directory artifacts\test-logs\ci\presentation-http
```

The presentation case reuses the existing provider-owned CLI fixture directly,
including its real native SQLite stores and synthetic provider HTTP transport.
The CLI test project references the production daemon, not another test project.
No source links, new friend identities or production hooks are needed. The
original complete-choice comparison uses the public `view-state` JSON response
instead of the daemon's internal serializer; the second view uses the same
explicit store/profile pair. These are runner adaptations, not reduced assertions.

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
  serving loops and browser-only diagnostic/process/scroll fixture branches are
  not copied. Default native HTTP assertions are unchanged.
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
