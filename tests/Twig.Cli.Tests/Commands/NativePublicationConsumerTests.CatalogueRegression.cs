using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeSourceCatalogueRefreshDoesNotAcquireProcessSchemaOrExecuteItems()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        _transport.ObservedPaths.Clear();
        var catalogue = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamA, ConfigurePlanning, default));
        catalogue.PlanningSources.ShouldNotBeNull().Entries.ShouldContain(source => source.Id == PrincipalTransport.SavedQuery);
        catalogue.Schema.ShouldBeNull("source catalogues are native list metadata, not a process/presentation refresh");
        catalogue.SchemaObservedAt.ShouldBeNull();
        _transport.ObservedPaths.ShouldAllBe(path => !path.Contains("/processes/", StringComparison.Ordinal)
            && !path.EndsWith("/fields", StringComparison.Ordinal) && !path.EndsWith("/layout", StringComparison.Ordinal)
            && !path.Contains("/workitemicons/", StringComparison.Ordinal)
            && !path.Contains("/wiql", StringComparison.Ordinal) && !path.Contains("/workitemsbatch", StringComparison.Ordinal));
        _transport.ItemReadIds.ShouldBeEmpty();
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }
}
