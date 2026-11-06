using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class PlanningConsumerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-planning-consumer-" + Guid.NewGuid().ToString("N"));
    private readonly Connection connection = new(new("ado"), "collection", "project", "actor");

    [Fact]
    public async Task ConfiguredAliasAndExplicitCanonicalRefreshesResolveNewestCalendarForOnlyTheirActualTeam()
    {
        Directory.CreateDirectory(root);
        var native = Required(await SqliteStore.CreateAsync(Path.Combine(root, "items.db")));
        var shared = new UserBenchStore(Path.Combine(root, "benches.db"));
        var provider = Substitute.For<IProvider>();
        var workspace = new CoreWorkspace(connection, provider, native, new ConfiguredPolicyProjectionSource(root),
            "actor", root, native.InstanceId, shared, "Configured alias");
        var now = DateTimeOffset.UtcNow;
        PlanningMetadata Calendar(string team, string path, DateTimeOffset instant)
            => new(instant, [], [new(path, now.AddDays(-1), now.AddDays(1))], [], team);
        Required(await shared.SavePlanningAsync(connection, "team-a", Calendar("team-a", "Project\\Original", now)));
        Required(await shared.SavePlanningAsync(connection, "team-b", Calendar("team-b", "Project\\B", now)));
        Required(await workspace.SavePlanningAsync(Calendar("team-a", "Project\\Configured refresh", now.AddSeconds(1)), default));
        Required(await workspace.PlanningAsync("team-a", default)).Metadata!.Iterations.Single().Path.ShouldBe("Project\\Configured refresh");
        Required(await workspace.PlanningAsync("team-b", default)).Metadata!.Iterations.Single().Path.ShouldBe("Project\\B");
        Required(await workspace.SavePlanningAsync("team-a", Calendar("team-a", "Project\\Explicit refresh", now.AddSeconds(2)), default));
        Required(await workspace.PlanningAsync("team-a", default)).Metadata!.Iterations.Single().Path.ShouldBe("Project\\Explicit refresh");
        Required(await workspace.PlanningAsync(default)).Metadata!.Iterations.Single().Path.ShouldBe("Project\\Explicit refresh");
        Required(await workspace.PlanningCalendarsAsync(default)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task ExplicitNativeBrowseMirrorsItemsWithoutReplacingSharedRulesOrWritingProposals()
    {
        Directory.CreateDirectory(Path.Combine(root, ".twig"));
        var native = Required(await SqliteStore.CreateAsync(Path.Combine(root, "items.db")));
        var shared = new UserBenchStore(Path.Combine(root, "benches.db"));
        var bench = Required(await shared.SaveAsync(new(Guid.NewGuid(), "Retained intent", []), 0));
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var item = new WorkItem(key, "Native.Type", FieldValues.Empty, new(key, "1"), true);
        var source = new PlanningSource(PlanningSourceKind.Backlog, "native.backlog", "Native backlog", "Native backlog", RequiresTeam: true);
        var provider = Substitute.For<IProvider>();
        provider.Capabilities.Returns(new ProviderCapabilities([], [], [], true, true));
        provider.BrowsePlanningAsync(connection, "Selected team", source.Kind, source.Id, "canonical-team", Arg.Any<CancellationToken>())
            .Returns(Result<PlanningBrowsePage>.Ok(new(source, new([item], [], null, true), DateTimeOffset.UtcNow, "canonical-team")));
        var workspace = new CoreWorkspace(connection, provider, native, new ConfiguredPolicyProjectionSource(root),
            "actor", root, native.InstanceId, shared, "Selected team");
        var browsed = Required(await workspace.BrowsePlanningAsync(source.Kind, source.Id, "canonical-team", default));
        browsed.Items.ShouldBe([key]);
        browsed.Team.ShouldBe("canonical-team");
        var mirror = Required(await native.ReadAsync(connection, default));
        mirror.Items.Single().Key.ShouldBe(key);
        mirror.Benches.ShouldBeEmpty();
        mirror.Proposals.ShouldBeEmpty();
        mirror.Journal.ShouldBeEmpty();
        BenchCodec.Serialize(Required(await shared.ReadAsync()).Benches.Single()).ShouldBe(BenchCodec.Serialize(bench));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("foreign-item")]
    [InlineData("substituted-source")]
    [InlineData("missing-team")]
    public async Task UntrustworthyNativeBrowseNeverCommitsAnEmptyOrBroaderSubstitute(string defect)
    {
        Directory.CreateDirectory(Path.Combine(root, ".twig"));
        var native = Required(await SqliteStore.CreateAsync(Path.Combine(root, "items.db")));
        var key = ItemKey.Remote(connection.Provider, defect == "foreign-item" ? "another-collection" : connection.ResourceScope, "42");
        var source = new PlanningSource(PlanningSourceKind.Backlog, defect == "substituted-source" ? "other" : "native.backlog", "Backlog", "Backlog");
        var provider = Substitute.For<IProvider>();
        provider.Capabilities.Returns(new ProviderCapabilities([], [], [], true, true));
        provider.BrowsePlanningAsync(connection, "Selected team", PlanningSourceKind.Backlog, "native.backlog", "canonical-team", Arg.Any<CancellationToken>())
            .Returns(Result<PlanningBrowsePage>.Ok(new(source,
                new([new(key, "Native.Type", FieldValues.Empty, new(key, "1"), true)], [], null, defect != "partial"),
                DateTimeOffset.UtcNow, defect == "missing-team" ? null : "canonical-team")));
        var workspace = new CoreWorkspace(connection, provider, native, new ConfiguredPolicyProjectionSource(root),
            "actor", root, native.InstanceId, planningTeam: "Selected team");
        (await workspace.BrowsePlanningAsync(PlanningSourceKind.Backlog, "native.backlog", "canonical-team", default)).IsSuccess.ShouldBeFalse();
        var stored = Required(await native.ReadAsync(connection, default));
        stored.Version.ShouldBe(0);
        stored.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task PersistentReferenceIntentSurvivesChangingAndEmptyExplicitRefreshesWhileCachedEvaluationRemainsLocal()
    {
        Directory.CreateDirectory(Path.Combine(root, ".twig"));
        var native = Required(await SqliteStore.CreateAsync(Path.Combine(root, "items.db")));
        var shared = new UserBenchStore(Path.Combine(root, "benches.db"));
        var query = new Query(QueryKind.SavedQuery, value: FieldValue.String("native.query"), planningTeam: "canonical-team");
        var bench = Required(await shared.SaveAsync(new(Guid.NewGuid(), "Saved query", [new(SelectorKind.Query, query: query, connection: connection)]), 0));
        var before = BenchCodec.Serialize(bench);
        WorkItem Item(string id)
        {
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, id);
            return new(key, "Native.Type", FieldValues.Empty, new(key, "1"), true);
        }
        var first = Item("42");
        var second = Item("43");
        var source = new PlanningSource(PlanningSourceKind.SavedQuery, "native.query", "Native query", "Shared/Native query");
        var provider = Substitute.For<IProvider>();
        provider.Capabilities.Returns(new ProviderCapabilities([], [], [], true, true));
        var refresh = 0;
        provider.BrowsePlanningAsync(connection, "canonical-team", source.Kind, source.Id, "canonical-team", Arg.Any<CancellationToken>())
            .Returns(_ => Result<PlanningBrowsePage>.Ok(new(source,
                new(++refresh == 1 ? [first] : refresh == 2 ? [second] : [], [], null, true),
                DateTimeOffset.UtcNow, "canonical-team")));
        var workspace = new CoreWorkspace(connection, provider, native, new ConfiguredPolicyProjectionSource(root),
            "actor", root, native.InstanceId, shared, "Selected team");
        foreach (var expected in new[] { new[] { first.Key }, new[] { second.Key }, Array.Empty<ItemKey>() })
        {
            Required(await workspace.QueryAsync(query, default)).Items.Select(item => item.Key).ShouldBe(expected);
            var reopened = new UserBenchStore(Path.Combine(root, "benches.db"));
            var cached = Required(await reopened.ReadMembershipsAsync(connection, "canonical-team", default));
            var schema = new ProcessSchema(connection, [], []) { Memberships = cached };
            var snapshot = Required(await native.ReadAsync(connection, default));
            Twig.Core.Core.ResolveBench(bench, snapshot, schema).Value.ShouldBe(expected);
            Required(await reopened.ReadMembershipsAsync(connection, "Another configured team", default)).ShouldBeEmpty();
            BenchCodec.Serialize(Required(await reopened.ReadAsync()).Benches.Single()).ShouldBe(before);
            snapshot.Benches.ShouldBeEmpty();
            snapshot.Proposals.ShouldBeEmpty();
            snapshot.Journal.ShouldBeEmpty();
        }
        refresh.ShouldBe(3, "cached evaluation must not call the provider");
        Required(await shared.ReadAsync()).Revision.ShouldBe(1);
        bench.Selectors.Single().Query!.Value!.Text.ShouldBe(source.Id, "the saved rule contains a source identity, not observed item IDs");
    }

    [Fact]
    public async Task EmptyResolvedIterationScopeNeverAcquiresABroaderProjectSuperset()
    {
        Directory.CreateDirectory(Path.Combine(root, ".twig"));
        var native = Required(await SqliteStore.CreateAsync(Path.Combine(root, "items.db")));
        var owner = new UserBenchStore(Path.Combine(root, "benches.db"));
        var provider = Substitute.For<IProvider>();
        var workspace = new CoreWorkspace(connection, provider, native, new ConfiguredPolicyProjectionSource(root),
            "actor", root, native.InstanceId, owner, "Selected team");
        var empty = new Query(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings([]));
        Required(await workspace.QueryAsync(empty, default)).Items.ShouldBeEmpty();
        var now = DateTimeOffset.UtcNow;
        Required(await owner.SavePlanningAsync(connection, "Selected team",
            new(now, [], [new("Project\\Past", now.AddDays(-4), now.AddDays(-2))], [], "canonical-team")));
        var noCurrent = new Query(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current"]), planningTeam: "canonical-team");
        Required(await workspace.QueryAsync(noCurrent, default)).Items.ShouldBeEmpty();
        await provider.DidNotReceiveWithAnyArgs().QueryAsync(default!, default!, default, default);
        Required(await native.ReadAsync(connection, default)).Version.ShouldBe(0);
    }

    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
