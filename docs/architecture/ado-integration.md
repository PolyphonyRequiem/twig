# Azure DevOps integration

The current work-item adapter is `Twig.Providers.Ado`, implementing Core's provider
port. `Twig.Infrastructure` supplies native enrolled credentials, binding and
workspace admission. The old `IAdoWorkItemService`, `SyncCoordinator`, and
automatic conflict-retry architecture are not supported alternate routes.

## Identity and connection

Enroll a named AAD identity with loopback PKCE or a named PAT identity, then
register an explicit organization/project binding. Fresh initialization pins the
observed native connection and actor. The provider cannot choose an ambient PAT,
an Azure CLI account, or another enrolled user after admission fails.

`auth status` and `connection status` describe recorded selection, not fresh
authenticated access. A refreshed read exercises the provider.
See [authentication](../features/authentication.md) for supported setup and renewal.

## Schema and reads

Discover types, fields, states, transitions, relations and supported rules from
the connected process. Retain schema gaps explicitly, including type-local gaps;
an unrelated incomplete type must not make all acquired types unusable.
Relevant missing or unknown rule evidence refuses an affected write.

Numeric IDs are native locators within the pinned connection. Core retains typed
field values and provider revisions. Cached `show` is distinct from explicit
provider refresh and query acquisition.
The [process description](../features/process-description.md) reports the acquired
Core schema, not a complete native form/layout export.

## Mutation and verification

Core prepares typed operations against actual observations, current schema and
declared policy. The ADO adapter uses revision-bound native writes where qualified.
Coupled state transitions and close-gate fields must be in **one atomic operation**,
not a state write followed by a field write. Values come from the connected
process, not a hardcoded process template.

Relationships retain their discovered kind and direction. Parent replacement
must be explicit; no host may infer endpoint ownership or silently remove an
existing parent. Resource links and local-only associations are separate effects.

Persisting Applying precedes dispatch. Acknowledgement, native readback, journal
verification and local draft settlement remain separate. Lost responses and
uncertain writes are not retried automatically under a different actor, digest,
or creation identity. `connection writes` is inspect-only evidence; it does not
settle Core execution.

For exact semantics and qualification boundaries, see the
[clean-core contract](clean-core-contract.md) and
[plan guide](../features/plans.md).
