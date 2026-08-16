using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Process;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Rendering;

namespace Twig.ProcessOverrides;

/// <summary>
/// Builds a throwaway service graph for a <c>--org</c>/<c>--project</c> override, so the
/// read-only process introspection commands can describe an arbitrary ADO project without a
/// workspace on disk.
/// </summary>
/// <remarks>
/// <para>
/// AB#216 / GitHub issue #368 Gap 1. Describing a second org's process previously required
/// <c>twig init --org X --project Y</c> in a throwaway directory, which writes cache, config
/// and auth state for a one-shot read.
/// </para>
/// <para>
/// 🔴 <b>This deliberately calls <see cref="NetworkServiceModule.AddTwigNetworkServices"/>
/// and NOT <c>AddConnectionServices</c>.</b> That is the whole mechanism behind acceptance 2
/// ("no cache/config/auth state is written"). <c>AddConnectionServices</c> is what registers
/// <c>SqliteCacheStore</c> and every store derived from it — it creates the database
/// directory on first resolution, and without a workspace it throws
/// <c>WorkspaceNotFoundException</c> anyway. Omitting it means the override path has no
/// persistence registered <i>at all</i>, so "writes nothing" is a property of the composition
/// rather than a discipline every future command has to remember.
/// </para>
/// <para>
/// 🔴 <b>The two commands are not symmetric in where their data comes from, and this is what
/// closes the gap.</b> <c>process layout</c> already reads live ADO through
/// <see cref="IFormLayoutProvider"/>, so it needs the flags and nothing else.
/// <c>process</c> normally reads the SQLite cache that <c>twig sync</c> fills, so under an
/// override it must fetch the same data <c>twig sync</c> fetches — via
/// <see cref="ProcessTypeSyncService"/>'s and <see cref="FieldDefinitionSyncService"/>'s own
/// source, <see cref="IIterationService"/> — and hold it in memory only. Hence the ephemeral
/// stores. Output is identical either way; only the latency and the freshness differ.
/// </para>
/// <para>
/// Auth resolves exactly as it does on the workspace path (acceptance 3): the auth method
/// comes from the user-scoped config that <see cref="TwigConfiguration"/> loads regardless of
/// workspace, and <c>AddTwigNetworkServices</c> builds the provider chain from it through the
/// same <c>AuthProviderFactory</c> every surface uses.
/// </para>
/// </remarks>
internal static class ProcessOverrideScope
{
    /// <summary>
    /// Builds an override <see cref="ServiceProvider"/> for <paramref name="org"/> /
    /// <paramref name="project"/>, carrying only what read-only process introspection needs.
    /// </summary>
    /// <param name="org">The ADO organization, as a name or a full org URL.</param>
    /// <param name="project">The ADO project name.</param>
    /// <param name="userPrefs">
    /// The user-scoped preferences (auth method, display) resolved from the ambient config.
    /// Passed in rather than loaded here so the caller owns config discovery.
    /// </param>
    public static ServiceProvider Build(string org, string project, TwigUserConfig userPrefs)
    {
        var config = new TwigConfiguration
        {
            RepoCoords = new TwigRepoConfig
            {
                Organization = org,
                Project = project,
            },
            UserPrefs = userPrefs,
        };

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices(stateEntries: null);

        // The two stores ProcessCommand consumes, fetched once and held in memory. Registered
        // as factories so nothing is fetched unless the command actually resolves them —
        // `process layout` never does.
        services.AddSingleton<IProcessTypeStore>(sp =>
            new EphemeralProcessTypeStore(
                FetchProcessTypes(sp.GetRequiredService<IIterationService>())));

        services.AddSingleton<IFieldDefinitionStore>(sp =>
            new EphemeralFieldDefinitionStore(
                sp.GetRequiredService<IIterationService>()
                    .GetFieldDefinitionsAsync().GetAwaiter().GetResult()));

        services.AddSingleton(sp => new ProcessCommand(
            activeItemResolver: null,
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<IFieldDefinitionStore>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<RendererFactory>()));

        services.AddSingleton(sp => new ProcessLayoutCommand(
            sp.GetRequiredService<IFormLayoutProvider>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<RendererFactory>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Fetches the same type + state + hierarchy data <see cref="ProcessTypeSyncService"/>
    /// persists, without persisting it.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ProcessTypeSyncService.SyncAsync</c>'s fetch-and-shape half. Kept as a
    /// distinct method rather than reusing that service because the service's contract is to
    /// SAVE, and calling a sync service on the path whose acceptance criterion is "writes
    /// nothing" would make the guarantee read as accidental.
    /// </remarks>
    private static IReadOnlyList<Domain.Aggregates.ProcessTypeRecord> FetchProcessTypes(
        IIterationService iterationService)
    {
        var typesWithStates = iterationService.GetWorkItemTypesWithStatesAsync()
            .GetAwaiter().GetResult() ?? [];

        var processConfig = iterationService.GetProcessConfigurationAsync()
            .GetAwaiter().GetResult() ?? new Domain.ValueObjects.ProcessConfigurationData();

        var parentChildMap = Domain.Services.Workspace.BacklogHierarchyService
            .InferParentChildMap(processConfig);

        var records = new List<Domain.Aggregates.ProcessTypeRecord>(typesWithStates.Count);
        foreach (var wit in typesWithStates)
        {
            parentChildMap.TryGetValue(wit.Name, out var children);

            records.Add(new Domain.Aggregates.ProcessTypeRecord
            {
                TypeName = wit.Name,
                States = wit.States
                    .Select(s => new Domain.ValueObjects.StateEntry(
                        s.Name,
                        Domain.Services.Process.StateCategoryResolver.ParseCategory(s.Category),
                        s.Color))
                    .ToList(),
                DefaultChildType = children is { Count: > 0 } ? children[0] : null,
                ValidChildTypes = children ?? [],
                ColorHex = wit.Color,
                IconId = wit.IconId,
            });
        }

        return records;
    }
}
