using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeSourceCatalogueCachePersistsAcrossReadersStoresAndProcessAndIsolatesTeamProjectAndIdentity()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        var absent = RequiredPlanning(await NativeSourceReader.ReadPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            null, ConfigurePlanning, default));
        absent.PlanningSourcesCache.Sources.ShouldBeNull();
        absent.PlanningSourcesCache.ObservedAt.ShouldBeNull();
        foreach (var team in new string?[] { PrincipalTransport.TeamA, PrincipalTransport.TeamB, null })
        {
            _transport.CatalogueName = team ?? "Project scope";
            var refreshed = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot,
                initial.Source, team, ConfigurePlanning, default));
            refreshed.PlanningSourcesCache.Sources!.Team.ShouldBe(team);
            refreshed.PlanningSourcesCache.ObservedAt.ShouldNotBeNull();
            refreshed.Schema.ShouldBeNull();
        }
        var catalogueReads = _transport.ObservedPaths.Count(path => path.EndsWith("/_apis/wit/queries", StringComparison.Ordinal));
        var authorityRequests = _transport.TotalRequests;
        foreach (var team in new string?[] { PrincipalTransport.TeamA, PrincipalTransport.TeamB, null })
        {
            var cached = RequiredPlanning(await NativeSourceReader.ReadPlanningSourcesAsync(paths.RepoRoot, initial.Source,
                team, ConfigurePlanning, default));
            var sources = cached.PlanningSourcesCache.Sources.ShouldNotBeNull();
            sources.Team.ShouldBe(team);
            sources.Entries.Single(source => source.Kind == PlanningSourceKind.SavedQuery).Name.ShouldBe(team ?? "Project scope");
            cached.PlanningSources.ShouldBeSameAs(sources);
            cached.Schema.ShouldBeNull();
            cached.SchemaObservedAt.ShouldBeNull();
            var reopened = UserBenchStore.ForRegistry(_bindings.RegistryPath);
            RequiredPlanning(await reopened.ReadPlanningSourcesAsync(connection, team, default)).ObservedAt
                .ShouldBe(cached.PlanningSourcesCache.ObservedAt);
        }
        _transport.TotalRequests.ShouldBeGreaterThan(authorityRequests, "cached list release still requires fresh actor/target validation");
        _transport.ObservedPaths.Count(path => path.EndsWith("/_apis/wit/queries", StringComparison.Ordinal)).ShouldBe(catalogueReads);
        var otherActor = new Connection(connection.Provider, connection.ResourceScope, connection.Destination, "another-native-actor");
        var otherProject = new Connection(connection.Provider, connection.ResourceScope, "another-project", connection.IdentityRef);
        foreach (var other in new[] { otherActor, otherProject })
            RequiredPlanning(await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadPlanningSourcesAsync(other,
                PrincipalTransport.TeamA, default)).Sources.ShouldBeNull();
        await AssertCatalogueRowsInFreshProcess(Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), 3);
        _transport.ItemReadIds.ShouldBeEmpty();
        _transport.QueryAfterIds.ShouldBeEmpty();
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData("partial", ErrorCode.Partial)]
    [InlineData("denied", ErrorCode.Unavailable)]
    [InlineData("unauthorized", ErrorCode.Unavailable)]
    public async Task NativeSourceCatalogueFailedRefreshPreservesCompleteRetainedMetadataAndErrorEvidence(string defect, ErrorCode expected)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        var before = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default)).PlanningSourcesCache;
        var captured = JsonSerializer.Serialize(before, UserBenchJson.Default.NativePlanningSourcesCache);
        _transport.CatalogueName = "Must not replace the complete list";
        _transport.CatalogueDefect = defect;
        var refused = await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(expected);
        if (defect != "partial") refused.Error.Evidence.ShouldContain(defect == "denied" ? "http:403" : "http:401");
        var retained = RequiredPlanning(await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadPlanningSourcesAsync(connection,
            PrincipalTransport.TeamA, default));
        JsonSerializer.Serialize(retained, UserBenchJson.Default.NativePlanningSourcesCache).ShouldBe(captured);
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.ItemReadIds.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("source")]
    [InlineData("store")]
    [InlineData("credential")]
    public async Task NativeSourceCatalogueAuthorityReplacementRefusesReleaseAndRefreshCommit(string change)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        var before = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default)).PlanningSourcesCache;
        var captured = JsonSerializer.Serialize(before, UserBenchJson.Default.NativePlanningSourcesCache);
        _transport.CatalogueName = "Must not escape replaced authority";
        if (change == "actor") _transport.ObservedPrincipalOverride = Sibling;
        else _transport.BeforeCatalogueResponse = async () =>
        {
            if (change == "credential")
            {
                var config = await TwigConfiguration.LoadSplitAsync(paths);
                var origin = await _bindings.ResolveAsync(config, paths);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _bindings.RegistryPath, Pooling = false }.ToString());
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE identities SET credential_ref=$replacement WHERE identity_id=$identity;";
                command.Parameters.AddWithValue("$replacement", "fixture-replaced-credential-reference");
                command.Parameters.AddWithValue("$identity", origin.Identity.IdentityId);
                (await command.ExecuteNonQueryAsync()).ShouldBe(1);
                return;
            }
            var marker = Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName);
            var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(marker), CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(change == "source"
                ? document with { PrincipalId = Sibling } : document with { DatabaseInstance = Guid.NewGuid() },
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        };
        (await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default)).IsSuccess.ShouldBeFalse();
        (await NativeSourceReader.ReadPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default)).IsSuccess.ShouldBeFalse();
        var retained = RequiredPlanning(await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadPlanningSourcesAsync(connection,
            PrincipalTransport.TeamA, default));
        JsonSerializer.Serialize(retained, UserBenchJson.Default.NativePlanningSourcesCache).ShouldBe(captured);
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.ItemReadIds.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData(PlanningSourceKind.SavedQuery)]
    [InlineData(PlanningSourceKind.Backlog)]
    public async Task NativeSourceCatalogueLightweightExecutionUsesRecordedMappingAndKeepsMembershipSeparate(PlanningSourceKind kind)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths, schema: true));
        var mapping = initial.Schema!.Value;
        var catalogue = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamB, ConfigurePlanning, default)).PlanningSourcesCache;
        _transport.ObservedPaths.Clear();
        var id = kind == PlanningSourceKind.SavedQuery ? PrincipalTransport.SavedQuery : "Fixture.Requirements";
        var browsed = RequiredPlanning(await NativeSourceReader.BrowsePlanningAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamB, kind, id, mapping, ConfigurePlanning, default));
        browsed.Schema.ShouldBeNull();
        browsed.SchemaObservedAt.ShouldBeNull();
        browsed.PlanningBrowse!.Team.ShouldBe(PrincipalTransport.TeamB);
        browsed.PlanningBrowse.Items.Single().NativeId.ShouldBe("43");
        browsed.Memberships.ShouldContain(value => value.Team == PrincipalTransport.TeamB && value.Kind == kind && value.Id == id);
        var retained = RequiredPlanning(await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadPlanningSourcesAsync(connection,
            PrincipalTransport.TeamB, default));
        JsonSerializer.Serialize(retained, UserBenchJson.Default.NativePlanningSourcesCache)
            .ShouldBe(JsonSerializer.Serialize(catalogue, UserBenchJson.Default.NativePlanningSourcesCache));
        _transport.ObservedPaths.ShouldAllBe(path => !path.Contains("/processes/", StringComparison.Ordinal)
            && !path.EndsWith("/fields", StringComparison.Ordinal) && !path.EndsWith("/layout", StringComparison.Ordinal)
            && !path.Contains("/workitemicons/", StringComparison.Ordinal));
        _transport.ObservedPaths.Count(path => path.EndsWith("/workitemrelationtypes", StringComparison.Ordinal)).ShouldBe(1);
        _transport.ObservedPaths.ShouldNotContain(path => path.EndsWith("/_apis/wit/queries", StringComparison.Ordinal)
            || path.EndsWith("/_apis/work/backlogs", StringComparison.Ordinal));
        RequiredPlanning(await store.ReadAsync(connection, default)).Items.Single().Key.NativeId.ShouldBe("43");
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
    }

    private static async Task AssertCatalogueRowsInFreshProcess(string path, int expected)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sqlite3,sys; db=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); print(db.execute('SELECT COUNT(*) FROM planning_sources').fetchone()[0])");
        start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            child.ExitCode.ShouldBe(0, await child.StandardError.ReadToEndAsync());
            (await child.StandardOutput.ReadToEndAsync()).Trim().ShouldBe(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000).ShouldBeTrue(); }
        }
    }
}
