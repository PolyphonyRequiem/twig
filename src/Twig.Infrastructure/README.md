# Twig.Infrastructure

Internal .NET 11 application composition for the current Twig CLI and Windows
native daemon. This project is **nonpackable** and makes no public NuGet or
general-purpose SDK compatibility promise. The supported public rendering
boundary is [Twig.RenderTree](../Twig.RenderTree/README.md), including its .NET 10
[DetailHost consumer](../../samples/Twig.DetailHost).

Configuration uses the committed `twig.json` and local `.twig/config` preferences
only. Retired and unknown configuration members are refused rather than silently
defaulted; area defaults use structured `defaults.areaPathEntries` exclusively.
Only a `twig.json` manifest identifies a workspace. Authentication uses explicit
named bindings and credential paths, never foreign MSAL-cache discovery or an
ambient PAT. Admission, principal freezing and uncertain-write fences remain
mandatory. Optional telemetry is a host-injected `ITelemetryClient` hook; this
assembly registers no sender and starts no telemetry HTTP requests.

## Current responsibilities

Infrastructure connects the closed native command/host catalog to the Core
workspace, provider implementations and SQLite store:

- Explicit identity enrollment, named bindings and immutable provider targets.
  Recorded selection is not fresh authenticated access or permission to write.
- Fresh workspace initialization and admission against the exact connection,
  actor, source and store. Missing or partial state is not silently adopted.
- Native Bench metadata, cached observations, local context and draft capture
  for supported CLI/daemon operations.
- Native editor/host orchestration, credential acquisition, Markdown conversion
  and optional privacy-allowlisted telemetry where consumed by current hosts.
- Transport evidence correlated to the original request, alongside Core's
  proposal, digest and execution journal.

`Twig.Core` owns typed values, discovered process schema, validation, merge,
proposals and execution outcomes. The ADO, GitHub and native providers retain
their explicit capability boundaries; Infrastructure must not turn an
unsupported provider operation into an implicit fallback.

## Current Core admission

Fresh ADO setup holds the newly created initialization handle and an exclusive
checkout operation lease. Its short-lived authority can acquire only the selected
metadata credential, rechecking the endpoint, attachment, selector revision and
actor before use and before recording. It is not a work-request or write guard.
Runtime admission instead requires the current `core-workspace.json`, its exact
registered root/store/connection, and the actual SQLite instance and workspace pin.
A missing marker, a copied locator or an unregistered replacement store cannot
grant admission; cache-mirror markers are not a second supported route.

## Mutation and observation boundaries

Local intent, preview, publication, verified effects and delivery into a local
view are distinct outcomes. Admission, schema/policy checks, observed revisions,
the exact reviewed digest and journal evidence remain mandatory. Multi-operation
application is ordered, not a cross-provider atomic transaction.

The Core journal is execution authority. `connection writes` inspects correlated
transport evidence; an HTTP acknowledgement, receipt, cached value or local
reset cannot certify an upstream outcome or authorize replay. Unknown creation
outcomes must retain their original identities and evidence rather than being
blindly retried.

Cached native reads describe recorded observations, not fresh upstream
authorization. Explicit online/schema reads retain their own provider and
freshness checks. Host presentation and source stamps do not grant publication
authority, and local result delivery must not dispatch another remote operation.

## Delivery boundaries

The CLI, native daemon and separately delivered canvas host are distinct
consumers. Daemon MCP is the current read-only interface, not the retired
standalone MCP host. There is no Infrastructure self-update feature, automatic
companion installation, legacy cache migration, old-binary adoption protocol,
database-rewind qualification or compatibility publisher to restore.

Developer-local application data is disposable. Prefer explicitly scoped
disposal and supported fresh initialization when local models change; never
discard credentials, upstream work or uncertain-write evidence as a substitute
for verifying an operation.

See the [current composition contract](../../docs/architecture/clean-core-contract.md),
[provider boundary](../../docs/architecture/provider-contract-boundary.md),
[proposal lifecycle](../../docs/features/proposals.md) and
[delivery documentation](../../docs/architecture/build-and-release.md).
Historical design/evidence remains historical, not an additional live API.
