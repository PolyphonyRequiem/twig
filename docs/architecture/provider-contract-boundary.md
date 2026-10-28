# Provider contracts in Domain

New work-management provider contracts belong in Infrastructure. Shared Domain
operations use the provider snapshot, identity, capability and execution ports;
selecting GitHub must not construct an ADO transport. The reflection guards in
`ProviderContractBoundaryTests` inventory all Domain types, including internal
and nested types, and freeze the public member shapes of these exact exceptions.
The public API analyzers separately protect shipped signatures and nullability.
No provider-name prefix is exempt.

## Frozen compatibility exceptions

These are existing public Domain package APIs, not neutral protocols:

| Contract | Reason retained |
| --- | --- |
| `IAdoWorkItemService` | Legacy integer/WIQL reads, seed publication, publish-intent recovery, history and artifact workflows. Its existing callers and shipped package API still require the native ADO contract. |
| `IRevisionBoundAdoWorkItemService` | Native strict-CAS primitives used only behind the ADO execution adapter; shared proposal code uses the neutral mutation port. |
| `IAdoGitService` | Existing ADO repository/PR and `vstfs` artifact workflows, not GitHub work-item discovery or execution. |
| `DeleteOutcome.AdoFailed` | Shipped result case used by existing delete consumers. |
| `PatchOutcome.AdoUnreachable` | Shipped result case used by existing patch consumers. |
| `FetchedFromAdo` | Shipped active-item result case and generated union surface. |

Moving these contracts and their remaining native orchestrators out of the
Domain package is a **deferred breaking package migration**, not work completed
by this change. A new member, overload, type or different shape cannot silently
expand the exception. It requires an explicit compatibility decision and review.
The integer/WIQL protocols have not become neutral merely by acquiring a port.

## Separate self-update exception

`IGitHubReleaseService`, `GitHubReleaseInfo` and `GitHubReleaseAssetInfo` serve
Twig binary self-update/changelog downloads. They are not work-item contracts.
Their exact names and public shapes are frozen in a separate allowlist; adding
GitHub work-management members to that API is not covered by this exception.

## Claim projection

The internal `IClaimProjection` receives an opaque scope and holder identity.
Its inputs and failure types no longer name ADO. The native ADO field mapping,
readback and identity verification remain in Infrastructure's `AdoClaimProjection`.
This refactor does not enable GitHub claims or change the legacy diagnostic
strings `ado-projection-failed` and `release-ado-projection-failed`.

The inventory regression fails on the previous internal ADO claim seam and
provider-named outcome cases. Positive and negative name controls prevent a
vacuous guard (including the neutral `ReadOnlySnapshot` spelling).
