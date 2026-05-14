# PolyphonyRequiem.Twig.Domain

Domain model for [twig](https://github.com/PolyphonyRequiem/twig) — a
high-performance .NET CLI for Azure DevOps work-item triage.

This package contains the pure domain layer: aggregates (`WorkItem`),
value objects, read models, domain services (sync, navigation, seed,
workspace, mutation), and the interfaces (`IWorkItemRepository`,
`IAdoWorkItemService`, `IAuthenticationProvider`, `IFieldDefinitionStore`,
…) that the infrastructure layer implements.

It has no I/O dependencies. Pair it with
[`PolyphonyRequiem.Twig.Infrastructure`](https://www.nuget.org/packages/PolyphonyRequiem.Twig.Infrastructure)
for SQLite-backed persistence and an Azure DevOps REST client.

## Composition

External consumers wire everything up through the
`Twig.Infrastructure.TwigServiceRegistration.AddTwigInfrastructure`
extension on `IServiceCollection`. See the
[twig repository](https://github.com/PolyphonyRequiem/twig) for the
end-to-end CLI that consumes these packages.

## License

MIT.
