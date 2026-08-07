using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tests;

/// <summary>
/// Builds a <see cref="ConnectionScope"/> over substitutes for tests.
/// </summary>
/// <remarks>
/// Composes the real <c>AddConnectionDomainServices</c> registrations on top of substituted
/// repositories and ADO clients, so the concrete domain services under test are wired exactly
/// as production wires them. The previous fixture hand-constructed each service, which made it
/// a fourth copy of the wiring that wayfinder 0016 deletes — a fixture that drifts from
/// production cannot catch production drift.
/// </remarks>
internal static class TestConnectionScope
{
    /// <summary>Builds a scope whose services resolve against the supplied substitutes.</summary>
    internal static ConnectionScope Build(
        Connection connection,
        TwigConfiguration config,
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository linkRepo,
        IIterationService iterationService,
        IProcessConfigurationProvider processConfigProvider,
        IPromptStateWriter promptStateWriter,
        IProcessTypeStore processTypeStore,
        IFieldDefinitionStore fieldDefinitionStore,
        ISeedLinkRepository seedLinkRepo,
        IPublishIdMapRepository publishIdMapRepo,
        ISeedPublishRulesProvider seedPublishRulesProvider,
        IUnitOfWork unitOfWork,
        ITrackingRepository? trackingRepo = null,
        IAdoGitService? adoGitService = null,
        IPublishIntentRepository? publishIntentRepo = null,
        IStagedIdentityRegistry? stagedIdentityRegistry = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(config);
        services.AddSingleton(TwigPaths.ForContext(
            Path.GetTempPath(), connection.Org, connection.Project));

        // Substituted boundaries — persistence and network.
        services.AddSingleton(contextStore);
        services.AddSingleton(workItemRepo);
        services.AddSingleton(adoService);
        services.AddSingleton(pendingChangeStore);
        services.AddSingleton(linkRepo);
        services.AddSingleton(iterationService);
        services.AddSingleton(processConfigProvider);
        services.AddSingleton(promptStateWriter);
        services.AddSingleton(processTypeStore);
        services.AddSingleton(fieldDefinitionStore);
        services.AddSingleton(seedLinkRepo);
        services.AddSingleton(publishIdMapRepo);
        services.AddSingleton(seedPublishRulesProvider);
        services.AddSingleton(unitOfWork);
        services.AddSingleton(trackingRepo ?? NSubstitute.Substitute.For<ITrackingRepository>());

        // ITrackingService is registered by AddConnectionServices, not by
        // AddConnectionDomainServices below. RefreshOrchestrator takes it as an OPTIONAL
        // dependency and silently returns 0 when it is absent — so without this line
        // twig_sync's tracked-tree refresh is a no-op in tests and any assertion about it
        // passes vacuously. Register it here, as production does.
        services.AddSingleton<ITrackingService>(sp => new TrackingService(
            sp.GetRequiredService<ITrackingRepository>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IProcessTypeStore>()));

        // Seed identity and the intent ledger are STATEFUL: ids are minted and read back
        // within a single test. NSubstitute would return 0 for every mint, silently turning
        // "allocate a distinct negative seed id" into "always 0" and hollowing the assertions
        // out. Back them with a real in-memory SQLite store, as the pre-0016 fixture did.
        var cacheStore = new SqliteCacheStore("Data Source=:memory:");
        services.AddSingleton(cacheStore);
        services.AddSingleton<IStagedIdentityRegistry>(
            stagedIdentityRegistry ?? new SqliteStagedIdentityRegistry(cacheStore));
        services.AddSingleton<IPublishIntentRepository>(
            publishIntentRepo ?? new SqlitePublishIntentRepository(cacheStore));

        // ADO #144. The Bench is STATEFUL for the same reason as the two above: selectors are
        // written and read back within one test, and a substitute returning an empty Bench would
        // make every evaluation match nothing while the assertions still looked plausible. The
        // iteration calendar is backed by the same real store so the sprint rule is answered from
        // local data — which is the behaviour under test, not a detail of the fixture.
        services.AddSingleton<IBenchRepository>(new SqliteBenchRepository(cacheStore));
        services.AddSingleton<IIterationCalendar>(new SqliteIterationCalendar(cacheStore));

        // The real shared wiring — the same call production makes.
        services.AddConnectionDomainServices();

        services.AddSingleton(sp => new McpPendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        if (adoGitService is not null)
        {
            services.AddSingleton(adoGitService);
            services.AddSingleton(sp => new BranchLinkService(
                sp.GetRequiredService<IAdoGitService>(),
                sp.GetRequiredService<IAdoWorkItemService>()));
        }

        return new ConnectionScope(connection, services.BuildServiceProvider());
    }
}
