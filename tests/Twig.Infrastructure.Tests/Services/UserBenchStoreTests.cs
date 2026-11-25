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
    public async Task OriginalIntakeRequiresTheExactDeliberateReviewAndPrivateSource()
    {
        var origin = new UpdateOrigin(Guid.NewGuid(), ado, "actor", "42");
        var review = Guid.NewGuid();
        var binding = new ReviewBinding(review, origin.Store, origin.Actor, "none");
        var key = Key(ado, "42");
        Required(await Store.ReadAsync());
        foreach (var wrong in new ReviewBinding?[] { null, binding with { Revision = Guid.NewGuid() },
            binding with { Store = Guid.NewGuid() }, binding with { Actor = "other" } })
        {
            var plan = new Plan(ado, "schema", 0, [new("update", OpKind.Batch, key, new(key, "v1"),
                FieldValues.Empty.Set("title", FieldValue.String("intent")))], review: wrong);
            (await Store.BindUpdateAsync(origin, review, "input", plan, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        }
        Required(await Store.FindUpdatesAsync(origin, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ConflictingSubmissionsKeepTheirOrderAcrossCredentialsAndMirrorsWhileDisjointWorkPasses()
    {
        var boot = Guid.NewGuid();
        Required(await Store.ReadAsync());
        async Task<OriginalUpdate> Submit(string item, string identity)
        {
            var connection = new Connection(ado.Provider, ado.ResourceScope, ado.Destination, identity);
            var origin = new UpdateOrigin(Guid.NewGuid(), connection, identity, item);
            var review = Guid.NewGuid();
            var key = Key(connection, item);
            var plan = new Plan(connection, "schema", 0, [new(review.ToString(), OpKind.Batch, key,
                new(key, "v1"), FieldValues.Empty.Set("title", FieldValue.String("value")))], review: new(review, origin.Store, origin.Actor, "none"));
            Required(await Store.BindUpdateAsync(origin, review, "input", plan, boot, default));
            return Required(await Store.OriginalUpdateAsync(origin, review, default));
        }
        var active = await Submit("42", "first");
        var earlier = await Submit("42", "second");
        var later = await Submit("42", "third");
        var disjoint = await Submit("43", "second");
        Task<Result<bool>> Claim(OriginalUpdate action) => Store.MoveUpdateAsync(action, "Claimed", boot, default);
        Required(await Claim(active)).ShouldBeTrue();
        Required(await Claim(earlier)).ShouldBeFalse();
        Required(await Claim(later)).ShouldBeFalse();
        Required(await Claim(disjoint)).ShouldBeTrue();
        Required(await Store.MoveUpdateAsync(active with { Identity = active.Identity with { State = "Claimed" } },
            "NotStarted", boot, default)).ShouldBeTrue();
        Required(await Claim(later)).ShouldBeFalse();
        Required(await Claim(earlier)).ShouldBeTrue();
    }

    [Fact]
    public async Task PostDispatchLocalContentionKeepsOriginalIdentityAndClaimsButNotProviderCapacity()
    {
        var boot = Guid.NewGuid();
        Required(await Store.ReadAsync());
        async Task<OriginalUpdate> Bind(string id)
        {
            var review = Guid.NewGuid();
            var key = Key(ado, id);
            var origin = new UpdateOrigin(Guid.NewGuid(), ado, "actor", id);
            var plan = new Plan(ado, "schema", 0, [new("update", OpKind.Batch, key, new(key, "v1"),
                FieldValues.Empty.Set("title", FieldValue.String("changed")))], review: new(review, origin.Store, origin.Actor, "none"));
            Required(await Store.BindUpdateAsync(origin, review, "hash", plan, boot, default));
            return Required(await Store.OriginalUpdateAsync(origin, review, default));
        }
        async Task<bool> Claim(OriginalUpdate u) => Required(await Store.MoveUpdateAsync(u, "Claimed", boot, default));
        var first = await Bind("1");
        (await Claim(first)).ShouldBeTrue();
        foreach (var id in new[] { "2", "3", "4" }) (await Claim(await Bind(id))).ShouldBeTrue();
        var fifth = await Bind("5");
        (await Claim(fifth)).ShouldBeFalse();
        var overlapping = await Bind("1");
        await Store.Barrier(first, boot).OpenAsync(first.Plan, first.Attempt, new("PATCH", "target", new string('a', 64), "actor", "project"), default);
        Required(await Store.MoveUpdateAsync(first with { Identity = first.Identity with { State = "Running" } }, "Pending", boot, default)).ShouldBeTrue();
        (await Claim(overlapping)).ShouldBeFalse();
        (await Claim(fifth)).ShouldBeTrue();
    }

    [Fact]
    public async Task SharedPolicyEndpointReadsCanOverlapButAnEndpointMutationWaitsForAllReaders()
    {
        var boot = Guid.NewGuid();
        Required(await Store.ReadAsync());
        var endpoint = Key(ado, "42");
        async Task<OriginalUpdate> Bind(string id, bool changeType)
        {
            var review = Guid.NewGuid();
            var origin = new UpdateOrigin(Guid.NewGuid(), ado, "actor", id);
            var item = Key(ado, id);
            var plan = new Plan(ado, "schema", 0, [new("change", OpKind.Batch, item, new(item, "v1"),
                FieldValues.Empty.Set("title", FieldValue.String("x")), changeType ? "new-type" : null)],
                review: new(review, origin.Store, origin.Actor, "capture") { Graph = [new(endpoint, "observed-type-and-edges")] });
            Required(await Store.BindUpdateAsync(origin, review, "input", plan, boot, default));
            return Required(await Store.OriginalUpdateAsync(origin, review, default));
        }
        var reader1 = await Bind("43", false);
        var reader2 = await Bind("44", false);
        var writer = await Bind("42", true);
        async Task<bool> Claim(OriginalUpdate u) => Required(await Store.MoveUpdateAsync(u, "Claimed", boot, default));
        async Task Release(OriginalUpdate u) => Required(await Store.MoveUpdateAsync(u with { Identity = u.Identity with { State = "Claimed" } }, "NotStarted", boot, default));
        (await Claim(reader1)).ShouldBeTrue();
        (await Claim(reader2)).ShouldBeTrue();
        (await Claim(writer)).ShouldBeFalse();
        await Release(reader1);
        (await Claim(writer)).ShouldBeFalse();
        await Release(reader2);
        (await Claim(writer)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentBindingsSurviveLostReplyAndLookupCannotDiscloseAnotherOriginsUpdate()
    {
        var scope = new UpdateOrigin(Guid.NewGuid(), ado, "principal", "42");
        var review = Guid.NewGuid();
        var boot = Guid.NewGuid();
        var key = Key(ado, "42");
        Plan Review(Guid revision) => new(ado, "schema", 1, [new("field", OpKind.Batch, key, new(key, "v1"),
            FieldValues.Empty.Set("field", FieldValue.String("private")))], review: new(revision, scope.Store, scope.Actor, "none"));
        var plan = Review(review);
        var store = Store;
        Required(await store.ReadAsync());
        var submitted = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
            Required(await Store.BindUpdateAsync(scope, review, "input", plan, boot, default)))));
        submitted.Distinct().Count().ShouldBe(1);
        (await Store.BindUpdateAsync(scope, review, "different", plan, boot, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        Required(await Store.FindUpdatesAsync(scope with { Actor = "other" }, default)).ShouldBeEmpty();
        Required(await Store.FindUpdatesAsync(scope, default)).Single().ShouldBe(submitted[0]);
        var other = Guid.NewGuid();
        Required(await Store.BindUpdateAsync(scope, other, "input", Review(other), boot, default));
        Required(await Store.FindUpdatesAsync(scope, default)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task SeedDeliveryPinsOnlyRemainingMembershipsInTheExactStoreAndConnection()
    {
        var store = Store;
        var seed = new BenchSeedReference(ado, Guid.NewGuid(), ItemKey.Local(Guid.NewGuid()));
        var other = seed with { StoreInstance = Guid.NewGuid() };
        Required(await store.ObserveAsync(Path.Combine(directory, "one"), ado, seed.StoreInstance, [], default));
        Required(await store.ObserveAsync(Path.Combine(directory, "two"), ado, other.StoreInstance, [], default));
        var target = Key(ado, "42");
        var pin = new Selector(SelectorKind.Pin, target, connection: ado);
        var first = Required(await store.SaveAsync(new(Guid.NewGuid(), "Already pinned", [pin]), 0));
        var second = Required(await store.SaveAsync(new(Guid.NewGuid(), "Also a member", []), 0));
        var removed = Required(await store.SaveAsync(new(Guid.NewGuid(), "No longer a member", []), 0));
        var deleted = Required(await store.SaveAsync(new(Guid.NewGuid(), "Deleted", []), 0));
        foreach (var bench in new[] { first, second, removed, deleted })
            Required(await store.SetSeedReferenceAsync(bench.Id, seed, true)).ShouldBeTrue();
        Required(await store.SetSeedReferenceAsync(first.Id, seed, true)).ShouldBeFalse();
        Required(await store.SetSeedReferenceAsync(first.Id, other, true)).ShouldBeTrue();
        Required(await store.SetSeedReferenceAsync(removed.Id, seed, false)).ShouldBeTrue();
        Required(await store.DeleteAsync(deleted.Id, 2));
        var before = Required(await store.ReadAsync());
        var impostor = new Connection(ado.Provider, ado.ResourceScope, ado.Destination, "another-identity");
        (await store.DeliverSeedReferenceAsync(seed with { Connection = impostor }, target)).IsSuccess.ShouldBeFalse();
        (await store.DeliverSeedReferenceAsync(seed, Key(github, "42"))).IsSuccess.ShouldBeFalse();
        Required(await Store.DeliverSeedReferenceAsync(seed, target)).ShouldBeTrue();
        Required(await Store.DeliverSeedReferenceAsync(seed, target)).ShouldBeFalse();
        var after = Required(await store.ReadAsync());
        after.Revision.ShouldBe(before.Revision + 1);
        foreach (var bench in new[] { first, second })
        {
            var updated = after.Benches.Single(value => value.Id == bench.Id);
            updated.Selectors.ShouldBe([pin]);
            updated.Connections.ShouldBe([ado]);
            updated.Revision.ShouldBe(before.Benches.Single(value => value.Id == bench.Id).Revision + 1);
        }
        after.Benches.Single(value => value.Id == removed.Id).Selectors.ShouldBeEmpty();
        after.Benches.Any(value => value.Id == deleted.Id).ShouldBeFalse();
        Required(await store.ReadSeedReferencesAsync(first.Id)).ShouldBe([other]);
        Required(await store.ReadSeedReferencesAsync(second.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AddingAndRemovingSeedsNeverChangesDeclaredConnections()
    {
        var store = Store;
        var seed = new BenchSeedReference(ado, Guid.NewGuid(), ItemKey.Local(Guid.NewGuid()));
        Required(await store.ObserveAsync(directory, ado, seed.StoreInstance, [], default));
        var derived = Required(await store.SaveAsync(new(Guid.NewGuid(), "Reference only", [], connections: [github]), 0));
        var declared = Required(await store.SaveAsync(new(Guid.NewGuid(), "Explicit ADO", [], connections: [ado]), 0));
        foreach (var bench in new[] { derived, declared })
        {
            Required(await store.SetSeedReferenceAsync(bench.Id, seed, true));
            var reopened = Required(await Store.ReadAsync()).Benches.Single(value => value.Id == bench.Id);
            reopened.Connections.ShouldBe(bench.Connections);
            Required(await Store.ReadSeedReferencesAsync(bench.Id)).ShouldBe([seed]);
            Required(await store.SetSeedReferenceAsync(bench.Id, seed, false));
            Required(await Store.ReadAsync()).Benches.Single(value => value.Id == bench.Id).Connections.ShouldBe(bench.Connections);
            Required(await Store.ReadSeedReferencesAsync(bench.Id)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task RemovingAndRestoringAConnectionDoesNotRestoreItsFormerSeedMembership()
    {
        var store = Store;
        var seed = new BenchSeedReference(ado, Guid.NewGuid(), ItemKey.Local(Guid.NewGuid()));
        Required(await store.ObserveAsync(directory, ado, seed.StoreInstance, [], default));
        var bench = Required(await store.SaveAsync(new(Guid.NewGuid(), "Changed source", [], connections: [ado]), 0));
        Required(await store.SetSeedReferenceAsync(bench.Id, seed, true));
        Required(await store.SaveAsync(new(bench.Id, bench.Name, []), 2));
        Required(await store.SaveAsync(new(bench.Id, bench.Name, [], connections: [ado]), 3));
        Required(await store.ReadSeedReferencesAsync(bench.Id)).ShouldBeEmpty();
        Required(await store.DeliverSeedReferenceAsync(seed, Key(ado, "42"))).ShouldBeFalse();
    }

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
    public async Task ReadingAnUnboundDeclarationDoesNotInitializeTheRepositoryOrUserStore()
    {
        Directory.CreateDirectory(directory);
        await Git(directory, "init", "--quiet");
        File.WriteAllText(Path.Combine(directory, "twig.json"), """{"provider":"twig","nativeDatabase":"shared"}""");
        var bindings = Required(await Store.ReadRepositoryAsync(directory));
        bindings.DeclaredConnection.ShouldBeNull();
        bindings.DeclaredSourceStatus.ShouldNotBeNull().ShouldContain("not registered");
        Directory.Exists(Path.Combine(directory, "user")).ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, ".twig")).ShouldBeFalse();
    }

    [Fact]
    public async Task TwoRepositoriesResolveTheirOwnDeclarationsWithoutChangingTheSharedBenchOrSavedAssignments()
    {
        var one = Directory.CreateDirectory(Path.Combine(directory, "one")).FullName;
        var two = Directory.CreateDirectory(Path.Combine(directory, "two")).FullName;
        await Git(one, "init", "--quiet");
        await Git(two, "init", "--quiet");
        async Task<Connection> Register(string name)
        {
            var source = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
            await Git(source, "init", "--quiet");
            using var bindings = new Twig.Infrastructure.Auth.ConnectionBindingService(Path.Combine(directory, "user"));
            var receipt = Required(await NativeWorkspaceSetup.InitializeAsync(
                new(source, "", "", null, null, "twig", null, null, null, null, null, Database: name),
                () => bindings, new(), default));
            var core = Required(await SqliteStore.OpenAsync(receipt.Document.DatabasePath, default, receipt.Source.StoreInstance));
            Required(await Store.View(core, source, core.InstanceId).ReadAsync(receipt.Source.Connection, default));
            return receipt.Source.Connection;
        }
        var a = await Register("alpha");
        var b = await Register("beta");
        static void Declare(string root, string name) =>
            File.WriteAllText(Path.Combine(root, "twig.json"), $$"""{"provider":"twig","nativeDatabase":"{{name}}"}""");
        Declare(one, "alpha");
        Declare(two, "beta");
        var bench = Required(await Store.SaveAsync(new(Guid.NewGuid(), "Shared", []), 0));
        Required(await Store.SaveRepositoryAsync(one, 0, [b], bench.Id, attachedBenches: [bench.Id]));
        Required(await Store.SaveRepositoryAsync(two, 0, [], bench.Id, attachedBenches: [bench.Id]));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string[] LocalFiles() => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Select(path => $"{path}:{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}")
            .Order(StringComparer.Ordinal).ToArray();
        var beforeRead = LocalFiles();
        var first = Required(await Store.ReadRepositoryAsync(one));
        first.DeclaredSourceStatus.ShouldBeNull();
        first.DeclaredConnection.ShouldBe(a);
        first.Connections.ShouldBe([b]);
        var second = Required(await Store.ReadRepositoryAsync(two));
        second.DeclaredConnection.ShouldBe(b);
        second.Connections.ShouldBeEmpty();
        LocalFiles().ShouldBe(beforeRead, "normal SQLite locks may create sidecars, not modify persistent application data");

        Declare(two, "alpha");
        var changed = Required(await Store.ReadRepositoryAsync(two));
        changed.Revision.ShouldBe(second.Revision);
        changed.DeclaredConnection.ShouldBe(a);
        var exact = Required(await Store.WorkspacesAsync()).Single(w => w.Connection == a);
        (await Store.SaveRepositoryAsync(two, changed.Revision, [b], bench.Id, expectedBenchId: bench.Id,
            expectedBenchRevision: bench.Revision, expectedWorkspace: exact with { Connection = b })).IsSuccess.ShouldBeFalse();
        Required(await Store.SaveRepositoryAsync(two, changed.Revision, [a], bench.Id, expectedBenchId: bench.Id,
            expectedBenchRevision: bench.Revision, expectedWorkspace: exact));
        Required(await Store.SaveRepositoryAsync(one, first.Revision, [b], bench.Id, excludedConnections: [a]));
        var excluded = Required(await Store.ReadRepositoryAsync(one));
        excluded.DeclaredSourceStatus.ShouldNotBeNull().ShouldContain("excluded");
        excluded.DeclaredConnection.ShouldBeNull();
        var duplicate = await Native("another-alpha-workspace");
        Required(await Store.View(duplicate.Store, duplicate.Root, duplicate.Store.InstanceId).ReadAsync(a, default));
        var ambiguous = Required(await Store.ReadRepositoryAsync(two));
        ambiguous.DeclaredConnection.ShouldBeNull();
        ambiguous.DeclaredSourceStatus.ShouldNotBeNull().ShouldContain("ambiguous workspace");
        Required(await Store.ReadAsync()).Benches.Single().Connections.ShouldBeEmpty();
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
        var generation = Required(await store.ReadAsync()).Revision;
        var now = DateTimeOffset.UtcNow;
        Required(await store.SavePlanningAsync(ado, "Team", new(now, ["Project\\Area"],
            [new("Project\\Absolute", now.AddDays(-1), now.AddDays(1))], [], "Team")));
        Required(await Store.ReadPlanningAsync(ado, "Team")).Metadata.ShouldNotBeNull().Iterations.Single().Path.ShouldBe("Project\\Absolute");
        Required(await Store.ReadPlanningAsync(ado, "Different team")).Metadata.ShouldBeNull();
        Required(await Store.ReadPlanningAsync(github, "Team")).Metadata.ShouldBeNull();
        BenchCodec.Serialize(Required(await Store.ReadAsync()).Benches.Single()).ShouldBe(before);
        Required(await Store.ReadAsync()).Revision.ShouldBe(generation + 1,
            "a calendar save invalidates acquired membership without editing the Bench definition");
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }
}
