using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeExplicitTeamPlanningRefreshBrowseAndPullPreserveOtherTeamsAndConfiguredSelection()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        var config = await TwigConfiguration.LoadSplitAsync(paths);
        var marker = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName));
        var shared = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var teams = RequiredPlanning(await NativeSourceReader.DiscoverPlanningTeamsAsync(paths.RepoRoot,
            initial.Source, ConfigurePlanning, default));
        teams.ShouldBe([new(PrincipalTransport.TeamA, "Actual team A"), new(PrincipalTransport.TeamB, "Actual team B")]);
        var a = RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamA, ConfigurePlanning, default));
        a.Planning!.Team.ShouldBe(PrincipalTransport.TeamA);
        var aCalendar = JsonSerializer.Serialize(a.Planning, Twig.Infrastructure.Serialization.TwigJsonContext.Default.PlanningMetadata);
        var b = RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamB, ConfigurePlanning, default));
        b.PlanningCalendars.Length.ShouldBe(2);
        JsonSerializer.Serialize(b.PlanningCalendars.Single(value => value.Team == PrincipalTransport.TeamA),
            Twig.Infrastructure.Serialization.TwigJsonContext.Default.PlanningMetadata).ShouldBe(aCalendar);
        _transport.ObservedPaths.Clear();
        var readA = RequiredPlanning(await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamA, ConfigurePlanning, default));
        readA.Planning!.Team.ShouldBe(PrincipalTransport.TeamA);
        readA.Schema.ShouldBeNull();
        readA.SchemaObservedAt.ShouldBeNull();
        _transport.ObservedPaths.ShouldContain(path => path.EndsWith("/_apis/wit/workitemtypes", StringComparison.Ordinal));
        _transport.ObservedPaths.ShouldAllBe(path => !path.EndsWith("/fields", StringComparison.Ordinal)
            && !path.EndsWith("/rules", StringComparison.Ordinal) && !path.EndsWith("/layout", StringComparison.Ordinal));
        var sources = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamB, ConfigurePlanning, default));
        sources.PlanningSources!.Team.ShouldBe(PrincipalTransport.TeamB);
        foreach (var team in new[] { PrincipalTransport.TeamA, PrincipalTransport.TeamB })
            RequiredPlanning(await NativeSourceReader.BrowsePlanningAsync(paths.RepoRoot, initial.Source, team,
                PlanningSourceKind.SavedQuery, PrincipalTransport.SavedQuery, ConfigurePlanning, default))
                .PlanningBrowse!.Items.Single().NativeId.ShouldBe(team == PrincipalTransport.TeamA ? "42" : "43");
        Query Current(string team) => new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current"]), planningTeam: team);
        var foreign = new Connection(new("ado"), "another-collection", "another-project", "another-actor");
        RequiredPlanning(await shared.SavePlanningAsync(foreign, PrincipalTransport.TeamA,
            new(DateTimeOffset.UtcNow, [], [new("Other Project\\Sprint", DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1))], [], PrincipalTransport.TeamA)));
        var bench = RequiredPlanning(await shared.SaveAsync(new(Guid.NewGuid(), "Multi-team", [
            new(SelectorKind.Query, query: Current(PrincipalTransport.TeamA), connection: connection),
            new(SelectorKind.Query, query: Current(PrincipalTransport.TeamB), connection: connection),
            new(SelectorKind.Query, query: Current(PrincipalTransport.TeamA), connection: foreign)]), 0));
        var context = new CoreHostContext(paths.RepoRoot, connection, store.InstanceId);
        var originalContext = RequiredPlanning(await context.ReadAsync(default));
        RequiredPlanning(await context.WriteAsync(originalContext, originalContext with { BenchId = bench.Id }, default));
        var beforeContext = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
        var pull = RunPlanning(paths, ["workspace", "--refresh", "-o", "json"]);
        pull.Code.ShouldBe(0, pull.Error);
        _transport.QueryAfterIds.ShouldBe([0, 0], "each exact-team rule must be acquired and locally evaluated independently");
        using (var rendered = JsonDocument.Parse(pull.Output))
            rendered.RootElement.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32())
                .ShouldBe([42, 43]);
        var observed = RequiredPlanning(await ReadSource(paths, initial.Source, schema: true));
        observed.PlanningCalendars.Length.ShouldBe(2, "another connection's same team ID must not enter this connection's calendar set");
        observed.Memberships.Select(value => value.Team).Order().ShouldBe(new[] { PrincipalTransport.TeamA, PrincipalTransport.TeamB }.Order());
        var keys = RequiredPlanning(Twig.Core.Core.ResolveBench(bench, observed.Snapshot, observed.Schema!.Value, DateTimeOffset.UtcNow));
        keys.Select(key => key.NativeId).ShouldBe(["42", "43"]);
        var foreignKey = ItemKey.Remote(foreign.Provider, foreign.ResourceScope, "1");
        var foreignItem = new WorkItem(foreignKey, "Foreign.Type",
            FieldValues.Empty.Set("System.IterationPath", FieldValue.String("Other Project\\Sprint")), new(foreignKey, "1"), true);
        var foreignSchema = new ProcessSchema(foreign, [], []) {
            PlanningCalendars = RequiredPlanning(await shared.ReadPlanningCalendarsAsync(foreign, default)) };
        RequiredPlanning(Twig.Core.Core.ResolveBench(bench, new(0, [foreignItem]), foreignSchema, DateTimeOffset.UtcNow))
            .ShouldBe([foreignKey]);
        var references = new Bench(Guid.NewGuid(), "Own-team memberships", [new(SelectorKind.Query,
            query: new(QueryKind.And, children: [Current(PrincipalTransport.TeamB), new(QueryKind.SavedQuery,
                value: FieldValue.String(PrincipalTransport.SavedQuery), planningTeam: PrincipalTransport.TeamB)]), connection: connection)]);
        RequiredPlanning(Twig.Core.Core.ResolveBench(references, observed.Snapshot, observed.Schema.Value, DateTimeOffset.UtcNow))
            .Single().NativeId.ShouldBe("43");
        _transport.TeamBPast = true;
        var refreshed = RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot,
            initial.Source, PrincipalTransport.TeamB, ConfigurePlanning, default));
        RequiredPlanning(Twig.Core.Core.ResolveBench(bench, refreshed.Snapshot, refreshed.Schema!.Value, DateTimeOffset.UtcNow))
            .Single().NativeId.ShouldBe("42");
        JsonSerializer.Serialize(refreshed.PlanningCalendars.Single(value => value.Team == PrincipalTransport.TeamA),
            Twig.Infrastructure.Serialization.TwigJsonContext.Default.PlanningMetadata).ShouldBe(aCalendar);
        BenchCodec.Serialize(RequiredPlanning(await shared.ReadAsync()).Benches.Single()).ShouldBe(BenchCodec.Serialize(bench));
        (await TwigConfiguration.LoadSplitAsync(paths)).Team.ShouldBe(config.Team);
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName))).ShouldBe(marker);
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(beforeContext);
        RequiredPlanning(await store.ReadAsync(connection, default)).Proposals.ShouldBeEmpty();
        RequiredPlanning(await store.ReadAsync(connection, default)).Journal.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PatchAttempts.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
    }

    [Fact]
    public async Task NativeExplicitTeamPlanningBacklogMembershipsKeepTheSameSourceIdSeparateForEachTeam()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        NativeSourceObservation? observed = null;
        foreach (var team in new[] { PrincipalTransport.TeamA, PrincipalTransport.TeamB })
        {
            observed = RequiredPlanning(await NativeSourceReader.BrowsePlanningAsync(paths.RepoRoot, initial.Source,
                team, PlanningSourceKind.Backlog, "Fixture.Requirements", ConfigurePlanning, default));
            observed.PlanningBrowse!.Items.Single().NativeId.ShouldBe(team == PrincipalTransport.TeamA ? "42" : "43");
        }
        observed.ShouldNotBeNull();
        observed.Memberships.Count(value => value.Kind == PlanningSourceKind.Backlog).ShouldBe(2);
        Query Backlog(string team) => new(QueryKind.Backlog, value: FieldValue.String("Fixture.Requirements"), planningTeam: team);
        var both = new Bench(Guid.NewGuid(), "Two team backlogs", [new(SelectorKind.Query,
            query: new(QueryKind.Or, children: [Backlog(PrincipalTransport.TeamA), Backlog(PrincipalTransport.TeamB)]), connection: connection)]);
        var keys = RequiredPlanning(Twig.Core.Core.ResolveBench(both, observed.Snapshot, observed.Schema!.Value));
        keys.Select(key => key.NativeId).ShouldBe(["42", "43"]);
        var visible = RequiredPlanning(Twig.Core.Core.ApplyViewExclusions(keys,
            [new(SelectorKind.Query, query: Backlog(PrincipalTransport.TeamB), connection: connection)], observed.Snapshot, observed.Schema.Value));
        visible.Single().NativeId.ShouldBe("42");
        RequiredPlanning(await store.ReadAsync(connection, default)).Proposals.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
    }

    [Fact]
    public async Task NativeExplicitTeamPlanningReadRefusesMissingCalendarOrTeamAndPreservesConfiguredCache()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        (await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot, initial.Source, PrincipalTransport.TeamB,
            ConfigurePlanning, default)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        (await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot, initial.Source, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ConfigurePlanning, default)).Error.Code.ShouldBe(ErrorCode.NotFound);
        var requests = _transport.TotalRequests;
        (await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot, initial.Source, "Actual team B",
            ConfigurePlanning, default)).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        _transport.TotalRequests.ShouldBe(requests, "an alias must be refused before composition or HTTP");
        (await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot, initial.Source with { PrincipalId = Sibling },
            PrincipalTransport.TeamA, ConfigurePlanning, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task NativeExplicitProjectPlanningNeverSubstitutesTheConfiguredTeam()
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default));
        var project = RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot, initial.Source,
            null, ConfigurePlanning, default));
        project.Planning!.Team.ShouldBeNull();
        project.Planning.Iterations.ShouldAllBe(iteration => !iteration.SelectedTeam);
        RequiredPlanning(await NativeSourceReader.ReadPlanningAsync(paths.RepoRoot, initial.Source, null, ConfigurePlanning, default))
            .Planning!.Team.ShouldBeNull();
        var sources = RequiredPlanning(await NativeSourceReader.DiscoverPlanningSourcesAsync(paths.RepoRoot,
            initial.Source, null, ConfigurePlanning, default));
        sources.PlanningSources!.Team.ShouldBeNull();
        RequiredPlanning(await NativeSourceReader.BrowsePlanningAsync(paths.RepoRoot, initial.Source, null,
            PlanningSourceKind.SavedQuery, PrincipalTransport.SavedQuery, ConfigurePlanning, default)).PlanningBrowse!.Team.ShouldBeNull();
        project.PlanningCalendars.Single().Team.ShouldBe(PrincipalTransport.TeamA);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-team")]
    [InlineData("actor")]
    [InlineData("marker")]
    [InlineData("store")]
    public async Task NativeExplicitTeamPlanningRefusesUnavailableOrReplacedAuthorityBeforeSavingCalendar(string defect)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        _transport.ExplicitPlanningTeams = true;
        var initial = RequiredPlanning(await ReadSource(paths));
        var a = RequiredPlanning(await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot, initial.Source,
            PrincipalTransport.TeamA, ConfigurePlanning, default));
        var shared = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = RequiredPlanning(await shared.SaveAsync(new(Guid.NewGuid(), "Preserved", []), 0));
        var expected = JsonSerializer.Serialize(a.PlanningCalendars, Twig.Infrastructure.Serialization.TwigJsonContext.Default.ImmutableArrayPlanningMetadata);
        var selected = defect == "missing" ? "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" : PrincipalTransport.TeamB;
        if (defect == "wrong-team") _transport.TeamLookupOverride = PrincipalTransport.TeamA;
        if (defect == "actor") _transport.ObservedPrincipalOverride = Sibling;
        if (defect is "marker" or "store")
            _transport.BeforeTeamCalendarResponse = async () =>
            {
                var path = Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName);
                var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                var next = defect == "marker" ? document with { PrincipalId = Sibling }
                    : document with { DatabaseInstance = Guid.NewGuid() };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(next, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
            };
        var result = await NativeSourceReader.RefreshPlanningAsync(paths.RepoRoot, initial.Source, selected, ConfigurePlanning, default);
        result.IsSuccess.ShouldBeFalse();
        RequiredPlanning(await shared.ReadPlanningAsync(connection, PrincipalTransport.TeamB)).Metadata.ShouldBeNull();
        var retainedA = RequiredPlanning(await shared.ReadPlanningAsync(connection, PrincipalTransport.TeamA)).Metadata!;
        JsonSerializer.Serialize(new[] { retainedA }.ToImmutableArray(), Twig.Infrastructure.Serialization.TwigJsonContext.Default.ImmutableArrayPlanningMetadata)
            .ShouldBe(expected);
        BenchCodec.Serialize(RequiredPlanning(await shared.ReadAsync()).Benches.Single()).ShouldBe(BenchCodec.Serialize(bench));
        RequiredPlanning(await store.ReadAsync(connection, default)).Version.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PatchAttempts.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
    }

    private void ConfigurePlanning(IServiceCollection services)
    {
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
    }

    private static T RequiredPlanning<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
}
