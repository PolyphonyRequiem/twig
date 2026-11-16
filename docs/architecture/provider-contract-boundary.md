# Provider contract boundary

Current provider-neutral semantics and ports belong in `Twig.Core`. Adapters in
`Twig.Providers.Ado`, `Twig.Providers.GitHub` and `Twig.Providers.Native` implement
only their declared native capabilities. SQLite persistence belongs in
`Twig.Store.Sqlite`; Infrastructure is internal application composition.

Selecting a provider must not construct another provider's transport or silently
substitute its credential/source. Identities preserve provider, resource scope
and opaque native ID; revision tokens are provider evidence, not interchangeable
integers or proof of atomic conditional mutation.

The legacy public Domain/Infrastructure SDK compatibility allowlists are not
current product contracts. `IGitHubReleaseService`, `GitHubReleaseInfo` and
`GitHubReleaseAssetInfo` no longer provide a self-update/changelog exception;
their old delivery island and CLI routes are removed. Infrastructure is
nonpackable. Only the RenderTree SDK retains a public package/API compatibility
promise.

Core owns schema/policy/admission, digest-bound execution and journals. Host
renderers, daemon transport, local metadata and correlated write evidence may
not create alternate publication or replay authority. See
[current composition](clean-core-contract.md) and [proposals](../features/proposals.md).
