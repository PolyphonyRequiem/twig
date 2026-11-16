# Current Core/native-host contract

This is the supported 1.0 composition boundary, not a compatibility catalog.
See [architecture](overview.md), the [delivery matrix](../../README.md#current-delivery)
and executable-matched help for current syntax.

## Composition and admission

Production composes `Twig.Core`, SQLite store, explicit provider adapters and
internal native Infrastructure. It never falls back to the legacy Domain/
Shared cache, pending flusher, publisher or plan executor. No Core marker means
data commands require fresh initialization. Existing or partial stores are not
silently adopted, reset or migrated.

Provider/source/principal identity is immutable evidence. ADO uses explicit
named enrollment/bindings; GitHub captures the selected named `gh` account or
explicit GitHub App identity, never an ambient credential fallback. Native
provider authority is durable user-owned state separate from client mirrors.

## Supported capabilities

- Process/schema discovery, cached explicit reads, explicit refresh and bounded
  queries.
- Current typed scalar/batch/note operations, local pending/save/discard and
  qualified seed lifecycle.
- Current v3 file-scoped proposal validate/preview/apply/status, exact digest
  confirmation and durable per-operation journal outcomes.
- Shared user-wide Bench definitions, rules/pins, contextual selection and
  static workspace projections. Native planning metadata and saved-query/
  backlog acquisition are explicit, not a full-screen application.
- Qualified ADO editor, navigation/tree, seed-chain, sync and relationship
  conveniences. Their existence does not imply GitHub parity.
- Offline version/help/skills and explicit native enrollment/binding utilities.

Structured machine calls require explicit targets. Machine workspace output
retains Bench scope/local drafts but excludes incidental active-context
relationships. Native status describes recorded selection, not fresh access.
`connection writes` is inspect-only transport evidence; only the Core journal
is execution authority.

## Invariants

Discover states, types, field requirements, transition gates and relationship
kinds dynamically. Incomplete evidence is unavailable, not compliant or empty.
Coupled gate fields and a state transition belong in one batch operation.

The proposal digest binds canonical intent. Core owns provider effects, policy,
merge/admission and operation journals. Capture/transport receipts, a successful
renderer, cached values or a local reset cannot establish remote outcome or
authorize replay. Unknown attempts retain original identities and evidence.

Benches store selection intent and are user-wide. Workspace exclusions are
view rules, not subtracting selectors. Changing one context does not select a
Bench everywhere or change provider work. Local drafts and authoritative items
remain distinct.

## Host and SDK boundary

The Windows daemon (`Bench.Server.exe`) owns authenticated same-machine service
lifetime. The read-only `mcp-read-v1` surface is current and deliberately has no
Save/Apply tools. Independent canvas assets/host retain their own verification
and packaging; they are not bundled CLI adapters.

`Twig.RenderTree` is the supported public SDK and keeps a .NET 10 consumer probe.
Infrastructure is internal, nonpackable application composition. Public CLR
visibility needed by the daemon is not an external SDK support promise.

## Removed surfaces

Quick `new`, standalone MCP, TUI/Herdr/OMP/item-companion adapters, captured UI
envelopes, policy-assessment/repair selection commands, native history/layout
exports, old proposal seed/latest/reconcile UX, `plan` and bare-area aliases,
migration/reset/reinitialize and identity/target hot-switch routes are retired.
Inert presentation/force/live/flat options are not retained compatibility knobs.
Historical ADRs, Wayfinder, plans and research explain old decisions but do not
restore removed commands, package APIs or delivery routes.
