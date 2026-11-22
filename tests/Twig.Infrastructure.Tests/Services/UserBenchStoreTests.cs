using Shouldly;
using NSubstitute;
using Twig.Core;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class UserBenchStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "twig-user-benches-" + Guid.NewGuid().ToString("N"));
    private readonly Connection ado = new(new("ado"), "collection", "project", "ado-identity");
    private readonly Connection github = new(new("github"), "https://api.github.com", "repository", "github-identity");
    private UserBenchStore Store => new(Path.Combine(directory, "user", "benches.db"));

    [Fact]
    public async Task ConnectionDependenciesSurviveEmptyMembershipAndScopedRemoval()
    {
        Directory.CreateDirectory(directory);
        var source = await Native("source");
        var saved = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Empty membership", [],
            connections: [ado, github]), 0));
        var reopened = Required(await Store.ReadAsync()).Benches.Single();
        reopened.Connections.ShouldBe([ado, github]);
        reopened.Selectors.ShouldBeEmpty();
        BenchCodec.Deserialize(BenchCodec.Serialize(reopened)).Connections.ShouldBe([ado, github]);
        var view = Store.View(source.Store, source.Root, source.Store.InstanceId);
        var captured = Required(await view.ReadAsync(ado, default));
        var selected = captured.Benches.Single();
        selected.Connections.ShouldBe([ado]);
        Required(await Store.CaptureAsync(ado, captured.Benches,
            [new(saved.Id, "Renamed without selecting work", [])], default));
        var after = Required(await Store.ReadAsync()).Benches.Single();
        after.Connections.ShouldBe([ado, github]);
        after.Selectors.ShouldBeEmpty();
        Required(await Store.ImportAsync([after], default));
        (await Store.ImportAsync([new(after.Id, after.Name, [], connections: [ado])], default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task RepositoryAttachmentsDistinguishUnconfiguredFromExplicitlyEmpty()
    {
        Directory.CreateDirectory(directory);
        await Git(directory, "init", "--quiet");
        var bench = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Existing", []), 0));
        Required(await Store.ReadRepositoryAsync(directory)).AttachmentsConfigured.ShouldBeFalse();
        (await Store.SaveRepositoryAsync(directory, 0, [], bench.Id, default, [], [])).Error.Code
            .ShouldBe(ErrorCode.InvalidInput);
        var empty = Required(await Store.SaveRepositoryAsync(directory, 0, [], null, default, [], []));
        empty.AttachmentsConfigured.ShouldBeTrue();
        empty.AttachedBenches.ShouldBeEmpty();
        var unchanged = Required(await Store.SaveRepositoryAsync(directory, empty.Revision, [], null, default, [], []));
        unchanged.Revision.ShouldBe(empty.Revision);
        var attached = Required(await Store.SaveRepositoryAsync(directory, empty.Revision, [], bench.Id, default, [bench.Id], []));
        attached.AttachedBenches.ShouldBe([bench.Id]);
        attached.DefaultBench.ShouldBe(bench.Id);
    }

    [Fact]
    public async Task RepositoryBindingsAreUserLocalReusableAndIndependentOfSavedBenchIntent()
    {
        Directory.CreateDirectory(directory);
        var repo = Path.Combine(directory, "code");
        var sibling = Path.Combine(directory, "sibling");
        var another = Path.Combine(directory, "other-code");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(another);
        await Git(repo, "init", "--quiet");
        await Git(repo, "-c", "user.name=Twig fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--allow-empty", "--quiet", "-m", "fixture");
        await Git(repo, "worktree", "add", "--quiet", "-b", "fixture-sibling", sibling);
        await Git(another, "init", "--quiet");
        var source = await Native("registered-source");
        Required(await Store.View(source.Store, source.Root, source.Store.InstanceId).ReadAsync(ado, default));
        var bench = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Shared", [], connections: [ado, github]), 0));
        var original = BenchCodec.Serialize(bench);
        Required(await Store.ReadRepositoryAsync(repo)).Revision.ShouldBe(0);
        var bindings = Required(await Store.SaveRepositoryAsync(repo, 0, [ado], bench.Id));
        var linked = Required(await Store.ReadRepositoryAsync(sibling));
        linked.RepositoryId.ShouldBe(bindings.RepositoryId);
        linked.Revision.ShouldBe(bindings.Revision);
        linked.Connections.ShouldBe(bindings.Connections);
        linked.DefaultBench.ShouldBe(bench.Id);
        var reused = Required(await Store.SaveRepositoryAsync(another, 0, [ado], bench.Id));
        reused.RepositoryId.ShouldNotBe(bindings.RepositoryId);
        (await Store.SaveRepositoryAsync(repo, 0, [], null)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await Store.SaveRepositoryAsync(repo, bindings.Revision, [github], bench.Id)).Error.Code.ShouldBe(ErrorCode.NotFound);
        var cleared = Required(await Store.SaveRepositoryAsync(sibling, bindings.Revision, [], null));
        cleared.Revision.ShouldBe(bindings.Revision + 1);
        cleared.DefaultBench.ShouldBeNull();
        Required(await Store.ReadRepositoryAsync(another)).Connections.ShouldBe([ado]);
        BenchCodec.Serialize(Required(await Store.ReadAsync()).Benches.Single()).ShouldBe(original);
        Required(await Store.WorkspacesAsync()).ShouldHaveSingleItem();
        Directory.GetFiles(repo).ShouldBeEmpty("all association data belongs in the user store, never in the code repository");
    }

    private static async Task Git(string root, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        _ = await output;
        process.ExitCode.ShouldBe(0, await error);
    }

    [Fact]
    public async Task WorktreesShareBenchDefinitionsWithoutCopyingThemToNativeStores()
    {
        Directory.CreateDirectory(directory);
        var a = await Native("repo");
        var b = await Native("worktree");
        var bench = new Bench(Guid.NewGuid(), "Chosen collection",
        [
            new(SelectorKind.Pin, Key(ado, "41"), connection: ado),
            new(SelectorKind.Subtree, Key(ado, "42"), hierarchyRelation: "native.parent-child", connection: ado),
            new(SelectorKind.Query, query: new(QueryKind.Under, "System.AreaPath", FieldValue.String("Project\\Area")), connection: ado),
            new(SelectorKind.Query, query: new(QueryKind.Equal, "System.IterationPath", FieldValue.String("Project\\Sprint")), connection: ado),
            new(SelectorKind.Query, query: new(QueryKind.Equal, "github.label", FieldValue.String("chosen")), connection: github),
        ]);
        var saved = Required(await Store.SaveAsync(bench, 0));
        var reopened = Required(await Store.ReadAsync());
        reopened.Benches.Single().Revision.ShouldBe(1);
        BenchCodec.Serialize(reopened.Benches.Single()).ShouldBe(BenchCodec.Serialize(saved));
        Required(await Store.SaveAsync(new(Guid.NewGuid(), "Another collection", []), 0));
        var viewA = Required(await Store.View(a.Store, a.Root, a.Store.InstanceId).ReadAsync(ado, default));
        var viewB = Required(await Store.View(b.Store, b.Root, b.Store.InstanceId).ReadAsync(ado, default));
        viewA.Benches.Select(value => value.Id).ShouldBe(viewB.Benches.Select(value => value.Id));
        viewA.Benches.Single(value => value.Id == saved.Id).Selectors.Length.ShouldBe(4);
        Required(await a.Store.ReadAsync(ado, default)).Benches.ShouldBeEmpty();
        Required(await b.Store.ReadAsync(ado, default)).Benches.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScopedCapturePreservesOtherConnectionsAndRejectsStaleEditsWithoutWritingNativeBenchCopies()
    {
        Directory.CreateDirectory(directory);
        var a = await Native("ado");
        var b = await Native("github");
        var bench = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Shared",
            [new(SelectorKind.Pin, Key(ado, "41"), connection: ado),
             new(SelectorKind.Pin, Key(github, "I_node"), connection: github)]), 0));
        var viewA = Store.View(a.Store, a.Root, a.Store.InstanceId);
        var viewB = Store.View(b.Store, b.Root, b.Store.InstanceId);
        var captureA = Required(await viewA.ReadAsync(ado, default));
        var captureB = Required(await viewB.ReadAsync(github, default));
        var provider = Substitute.For<IProvider>();
        var workspace = new CoreWorkspace(ado, provider, viewA, new ConfiguredPolicyProjectionSource(a.Root),
            "actor", a.Root, a.Store.InstanceId, Store);
        var selected = captureA.Benches.Single();
        Required(await workspace.CaptureBenchesAsync(captureA,
            [new(bench.Id, "Renamed", selected.Selectors.Add(new(SelectorKind.Pin, Key(ado, "43"))))], default));
        var changed = Required(await Store.ReadAsync()).Benches.Single();
        changed.Name.ShouldBe("Renamed");
        changed.Selectors.Length.ShouldBe(3);
        changed.Selectors.ShouldContain(selector => selector.Connection == github && selector.Item == Key(github, "I_node"));
        var stale = await Store.CaptureAsync(github, captureB.Benches,
            [new(bench.Id, "Stale name", captureB.Benches.Single().Selectors)], default);
        stale.IsSuccess.ShouldBeFalse();
        stale.Error.Code.ShouldBe(ErrorCode.Conflict);
        var current = Required(await viewA.ReadAsync(ado, default));
        Required(await viewA.CommitAsync(ado, current.Version, new(current.Version + 1, benches: current.Benches), default));
        var native = Required(await a.Store.ReadAsync(ado, default));
        native.Benches.ShouldBeEmpty("the native mirror must not become a second authoritative Bench store");
        native.Journal.ShouldBeEmpty();
        native.Plans.ShouldBeEmpty();
        Required(await Store.ReadAsync()).Benches.Single().Name.ShouldBe("Renamed");
        provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingDefinitionsImportOnceAndDeletionCannotBeUndoneByOldWorkspaceOrIdReuse()
    {
        Directory.CreateDirectory(directory);
        var native = await Native("existing");
        var old = new Bench(Guid.NewGuid(), "User-created earlier", [new(SelectorKind.Pin, Key(ado, "41"))]);
        Required(await native.Store.CommitAsync(ado, 0, new(1, benches: [old]), default));
        var view = Store.View(native.Store, native.Root, native.Store.InstanceId);
        Required(await view.ReadAsync(ado, default)).Benches.Single().Id.ShouldBe(old.Id);
        var imported = Required(await Store.ReadAsync()).Benches.Single();
        Required(await Store.DeleteAsync(imported.Id, imported.Revision));
        Required(await Store.View(native.Store, native.Root, native.Store.InstanceId).ReadAsync(ado, default))
            .Benches.ShouldBeEmpty();
        Required(await native.Store.ReadAsync(ado, default)).Benches.Single().Id.ShouldBe(old.Id);
        (await Store.SaveAsync(new(old.Id, "Replayed identity", []), 0)).Error.Code.ShouldBe(ErrorCode.Conflict);
        var replacement = Required(await Store.SaveAsync(new(Guid.NewGuid(), old.Name, []), 0));
        replacement.Id.ShouldNotBe(old.Id);
        Required(await Store.ReadAsync()).Benches.Single().Id.ShouldBe(replacement.Id);
        Directory.Delete(native.Root, recursive: true);
        Required(await Store.ReadAsync()).Benches.Single().Id.ShouldBe(replacement.Id);
    }

    [Fact]
    public async Task ReplacedLibraryCannotAdoptCapturedMutationAndCannotBlockUnrelatedNativeSnapshots()
    {
        Directory.CreateDirectory(directory);
        var bound = Store;
        var first = Required(await bound.SaveAsync(new(Guid.NewGuid(), "Original", []), 0));
        var captured = Required(await bound.ReadAsync());
        var path = Path.Combine(directory, "user", "benches.db");
        File.Move(path, Path.Combine(directory, "original.db"));
        var replacement = new UserBenchStore(path);
        Required(await replacement.SaveAsync(new(first.Id, "Replacement", []), 0));
        Required(await replacement.ReadAsync()).InstanceId.ShouldNotBe(captured.InstanceId);
        (await bound.DeleteAsync(first.Id, first.Revision)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        Required(await replacement.ReadAsync()).Benches.Single().Name.ShouldBe("Replacement");
        var native = await Native("repo");
        var provider = Substitute.For<IProvider>();
        var workspace = new CoreWorkspace(ado, provider, native.Store, new ConfiguredPolicyProjectionSource(native.Root),
            "actor", native.Root, native.Store.InstanceId, bound);
        Required(await workspace.SnapshotAsync(default)).Version.ShouldBe(0);
        (await workspace.BenchSnapshotAsync(default)).IsSuccess.ShouldBeFalse();
        provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task WorkspaceExclusionsFilterTheViewWithoutChangingSharedPinsOrOtherContexts()
    {
        Directory.CreateDirectory(directory);
        var a = await Native("repo");
        var b = await Native("worktree");
        var first = Key(ado, "41");
        var root = Key(ado, "42");
        var child = Key(ado, "43");
        var bench = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Shared",
            [new(SelectorKind.Pin, first, connection: ado),
             new(SelectorKind.Subtree, root, hierarchyRelation: "native.parent-child", connection: ado)]), 0));
        WorkItem Item(ItemKey key) => new(key, "Native.Type", FieldValues.Empty, new(key, "1"), true);
        var snapshot = new StoreSnapshot(0, [Item(first), Item(root), Item(child)],
            [new(root, child, "native.parent-child", RelationshipOrigin.Provider)]);
        var schema = new ProcessSchema(ado, [], [new("native.parent-child", RelationShape.Hierarchy, true)]);
        var members = Required(Twig.Core.Core.ResolveBench(bench, snapshot, schema));
        members.Length.ShouldBe(3);
        var saved = Required(await Store.SaveViewRulesAsync(a.Root, ado, 0,
            [new(SelectorKind.Pin, first), new(SelectorKind.Subtree, root, hierarchyRelation: "native.parent-child")]));
        var visible = Required(Twig.Core.Core.ApplyViewExclusions(members, saved.Exclusions, snapshot, schema));
        visible.ShouldBeEmpty();
        Required(await Store.ReadViewRulesAsync(b.Root, ado)).Exclusions.ShouldBeEmpty();
        Required(await Store.ReadViewRulesAsync(a.Root, github)).Exclusions.ShouldBeEmpty();
        (await Store.SaveViewRulesAsync(a.Root, ado, 0, [])).Error.Code.ShouldBe(ErrorCode.Conflict);
        BenchCodec.Serialize(Required(await Store.ReadAsync()).Benches.Single()).ShouldBe(BenchCodec.Serialize(bench));
        var cleared = Required(await Store.SaveViewRulesAsync(a.Root, ado, saved.Revision, []));
        Required(Twig.Core.Core.ApplyViewExclusions(members, cleared.Exclusions, snapshot, schema)).ShouldBe(members);
        Required(await a.Store.ReadAsync(ado, default)).Version.ShouldBe(0);
    }

    [Fact]
    public async Task CanonicalNameAndRevisionGuardsSurviveReopenAndForeignFilesAreNeverAdopted()
    {
        Directory.CreateDirectory(directory);
        var first = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Caf\u00e9", []), 0));
        (await Store.SaveAsync(new(Guid.NewGuid(), "cafe\u0301", []), 0)).Error.Code.ShouldBe(ErrorCode.Conflict);
        var changed = Required(await Store.SaveAsync(new(first.Id, "Renamed", []), first.Revision));
        (await Store.DeleteAsync(first.Id, first.Revision)).Error.Code.ShouldBe(ErrorCode.Conflict);
        Required(await Store.ReadAsync()).Benches.Single().Revision.ShouldBe(changed.Revision);
        var foreign = Path.Combine(directory, "foreign.db");
        await File.WriteAllTextAsync(foreign, "not a Twig Bench store");
        var read = await new UserBenchStore(foreign).ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        (await File.ReadAllTextAsync(foreign)).ShouldBe("not a Twig Bench store");
    }

    private async Task<(string Root, SqliteStore Store)> Native(string name)
    {
        var root = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(root, ".twig"));
        return (root, Required(await SqliteStore.CreateAsync(Path.Combine(root, ".twig", "core.db"))));
    }

    [Fact]
    public async Task ExplicitImportIsAtomicIdempotentAndDoesNotOverwriteConflictingIntentOrTombstones()
    {
        Directory.CreateDirectory(directory);
        var store = Store;
        var first = new Bench(Guid.NewGuid(), "First", [new(SelectorKind.Pin, Key(ado, "41"), connection: ado)]);
        var second = new Bench(Guid.NewGuid(), "Second", [new(SelectorKind.Query,
            query: new(QueryKind.Under, "System.AreaPath", FieldValue.String("Project\\Area")), connection: ado)]);
        var imported = Required(await store.ImportAsync([first, second]));
        var repeated = Required(await store.ImportAsync([first, second]));
        repeated.Revision.ShouldBe(imported.Revision);
        repeated.Benches.Length.ShouldBe(2);
        var third = new Bench(Guid.NewGuid(), "Third", []);
        (await store.ImportAsync([third, new(first.Id, "Conflicting name", first.Selectors)])).Error.Code.ShouldBe(ErrorCode.Conflict);
        Required(await store.ReadAsync()).Benches.ShouldNotContain(bench => bench.Id == third.Id);
        var retained = repeated.Benches.Single(bench => bench.Id == first.Id);
        Required(await store.DeleteAsync(retained.Id, retained.Revision));
        (await store.ImportAsync([first])).Error.Code.ShouldBe(ErrorCode.Conflict);
        Required(await store.ReadAsync()).Benches.ShouldNotContain(bench => bench.Id == first.Id);
    }
    [Fact]
    public async Task PlanningIntentAndCalendarPersistSeparatelyWithoutMaterializingOrChangingBenchRules()
    {
        Directory.CreateDirectory(directory);
        var store = Store;
        Required(await store.ReadPlanningAsync(ado, "Team")).Metadata.ShouldBeNull();
        var rule = new Selector(SelectorKind.Query,
            query: new(QueryKind.And, children:
            [
                new(QueryKind.PathEqual, "System.AreaPath", FieldValue.String("Project\\Area")),
                new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@current+1", "Project\\Absolute"]), planningTeam: "Team"),
            ]), connection: ado);
        var bench = Required(await store.SaveAsync(new(Guid.NewGuid(), "Planning rules", [rule]), 0));
        var before = BenchCodec.Serialize(bench);
        var now = DateTimeOffset.UtcNow;
        Required(await store.SavePlanningAsync(ado, "Team", new(now, ["Project\\Area"],
            [new("Project\\Absolute", now.AddDays(-1), now.AddDays(1))], [], "Team")));
        Required(await Store.ReadPlanningAsync(ado, "Team")).Metadata.ShouldNotBeNull().Iterations.Single().Path.ShouldBe("Project\\Absolute");
        Required(await Store.ReadPlanningAsync(ado, "Different team")).Metadata.ShouldBeNull();
        Required(await Store.ReadPlanningAsync(github, "Team")).Metadata.ShouldBeNull();
        BenchCodec.Serialize(Required(await Store.ReadAsync()).Benches.Single()).ShouldBe(before);
        Required(await Store.ReadAsync()).Revision.ShouldBe(1);
        bench.Selectors.Single().Query!.Children[1].Value!.Values.ShouldContain("@Current+1");
    }

    [Fact]
    public async Task NativeQueryConsumerResolvesPersistedTeamIntentWithoutCopyingDefinitionsIntoItemMirrors()
    {
        Directory.CreateDirectory(directory);
        var native = await Native("planned-query");
        var owner = Store;
        var now = DateTimeOffset.UtcNow;
        var rule = new Query(QueryKind.And, children:
        [
            new(QueryKind.PathEqual, "System.AreaPath", FieldValue.String("Project\\Area")),
            new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current+1"]), planningTeam: "canonical-team"),
        ]);
        var bench = Required(await owner.SaveAsync(new(Guid.NewGuid(), "Native planning", [new(SelectorKind.Query, query: rule, connection: ado)]), 0));
        Required(await owner.SavePlanningAsync(ado, "Configured team", new(now, ["Project\\Area"],
            [new("Project\\Current", now.AddDays(-1), now.AddDays(1)), new("Project\\Next", now.AddDays(2), now.AddDays(3))], [], "canonical-team")));
        var provider = Substitute.For<IProvider>();
        provider.Capabilities.Returns(new ProviderCapabilities([], [], [], true, true));
        Query? dispatched = null;
        provider.QueryAsync(ado, Arg.Any<Query>(), null, Arg.Any<CancellationToken>()).Returns(call =>
        {
            dispatched = call.Arg<Query>();
            return Result<QueryPage>.Ok(new([], [], null, true));
        });
        var workspace = new CoreWorkspace(ado, provider, native.Store, new ConfiguredPolicyProjectionSource(native.Root),
            "actor", native.Root, native.Store.InstanceId, owner, "Configured team");
        Required(await workspace.QueryAsync(rule, default)).Complete.ShouldBeTrue();
        dispatched.ShouldNotBeNull().Children[1].Kind.ShouldBe(QueryKind.PathEqual);
        dispatched.Children[1].Value!.Text.ShouldBe("Project\\Next");
        BenchCodec.Serialize(Required(await owner.ReadAsync()).Benches.Single()).ShouldBe(BenchCodec.Serialize(bench));
        Required(await native.Store.ReadAsync(ado, default)).Benches.ShouldBeEmpty();
        var different = new CoreWorkspace(ado, provider, native.Store, new ConfiguredPolicyProjectionSource(native.Root),
            "actor", native.Root, native.Store.InstanceId, owner, "Another configured team");
        Required(await different.QueryAsync(rule, default)).Complete.ShouldBeTrue("retained canonical intent is independent of the configured team");
        dispatched!.Children[1].Value!.Text.ShouldBe("Project\\Next");
        var unavailable = new Query(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current"]),
            planningTeam: "uncached-native-team");
        (await different.QueryAsync(unavailable, default)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        await provider.Received(2).QueryAsync(ado, Arg.Any<Query>(), null, Arg.Any<CancellationToken>());
    }

    private static ItemKey Key(Connection connection, string id)
        => ItemKey.Remote(connection.Provider, connection.ResourceScope, id);
    private static T Required<T>(Result<T> value)
    {
        value.IsSuccess.ShouldBeTrue(value.IsSuccess ? "" : value.Error.Message);
        return value.Value;
    }
    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }
}
