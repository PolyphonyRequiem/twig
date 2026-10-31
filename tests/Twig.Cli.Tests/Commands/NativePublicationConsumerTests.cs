using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Policy;
using Xunit;
using CoreModel = Twig.Core;
using CleanStore = Twig.Store.Sqlite.SqliteStore;
using Twig.Infrastructure.Services;

namespace Twig.Cli.Tests.Commands;

/// <summary>Native receipts over real attached stores and independently maintained, principal-private remote state.</summary>
public sealed class NativePublicationConsumerTests : IAsyncLifetime
{
    private const string Actor = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Sibling = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ActorPat = "fixture-actor-pat";
    private const string SiblingPat = "fixture-sibling-pat";
    private readonly string _temp = CanonicalTempRoot.Create("twig-publication-consumer-");
    private readonly PrincipalTransport _transport = new();
    private readonly List<ServiceProvider> _runtimes = [];
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;
    private SqliteSystemWorktreeRegistry _systemRegistry = null!;
    private string Home => Path.Combine(_temp, "home");

    public Task InitializeAsync()
    {
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, patHttpClient: _http);
        _systemRegistry = new SqliteSystemWorktreeRegistry(_bindings.RegistryPath, TimeProvider.System);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        _systemRegistry.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _bindings.RegistryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString());
        SqliteConnection.ClearPool(pool);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CoreBenchCrudPreservesSelectorKindsAndNeverResolvesLegacyWriters()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "bench-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["bench", "list", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.BenchCreate("Review scope", "json")).ShouldBe(0, stderr.ToString());
            var created = (await store.ReadAsync(connection, default)).Value.Benches.ShouldHaveSingleItem();
            (await admitted.Workspace.Context.ReadAsync(default)).Value.BenchId.ShouldBeNull();
            (await commands.BenchCreate("review SCOPE", "json")).ShouldBe(1);
            (await commands.BenchSwitch("missing scope", "json")).ShouldBe(1);
            (await commands.BenchSwitch("Review scope", "json", expectBench: created.Id.ToString("D"))).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrack(42, "json", expectBench: created.Id.ToString("D"))).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrackTree(42, "json")).ShouldBe(0, stderr.ToString());
            var snapshot = (await store.ReadAsync(connection, default)).Value;
            snapshot.Benches.Single().Selectors.Select(selector => selector.Kind).ShouldBe([CoreModel.SelectorKind.Pin, CoreModel.SelectorKind.Subtree]);
            var query = new CoreModel.Selector(CoreModel.SelectorKind.Query, query: new(CoreModel.QueryKind.All));
            (await admitted.Workspace.CaptureBenchesAsync(snapshot,
                [new(created.Id, created.Name, snapshot.Benches.Single().Selectors.Add(query))], default)).IsSuccess.ShouldBeTrue();
            (await commands.WorkspaceUntrack(42, "json", mode: "SiNgLe")).ShouldBe(0, stderr.ToString());
            var unpinned = (await store.ReadAsync(connection, default)).Value.Benches.Single();
            unpinned.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Query);
            unpinned.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Subtree);
            unpinned.Selectors.ShouldNotContain(selector => selector.Kind == CoreModel.SelectorKind.Pin);
            _transport.CoreMetadataEnabled = false;
            (await commands.WorkspaceUntrack(42, "json", mode: "TrEe")).ShouldBe(0, stderr.ToString());
            _transport.CoreMetadataEnabled = true;
            var final = (await store.ReadAsync(connection, default)).Value;
            final.Benches.Single().Selectors.ShouldHaveSingleItem().Kind.ShouldBe(CoreModel.SelectorKind.Query);
            var version = final.Version;
            (await commands.WorkspaceTrack(43, "json", expectBench: Guid.NewGuid().ToString("D"))).ShouldBe(1);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(version);
            (await commands.WorkspaceTrack(42, "json")).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrackTree(42, "json")).ShouldBe(0, stderr.ToString());
            (await store.ReadAsync(connection, default)).Value.Benches.Single().Selectors.Count(selector =>
                selector.Kind is CoreModel.SelectorKind.Pin or CoreModel.SelectorKind.Subtree).ShouldBe(2);
            _transport.CoreMetadataEnabled = false;
            (await commands.WorkspaceUntrack(42, "json")).ShouldBe(0, stderr.ToString());
            _transport.CoreMetadataEnabled = true;
            var removed = (await store.ReadAsync(connection, default)).Value;
            removed.Benches.Single().Selectors.ShouldHaveSingleItem().Kind.ShouldBe(CoreModel.SelectorKind.Query);
            (await commands.WorkspaceUntrack(42, "json")).ShouldBe(0, stderr.ToString());
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(removed.Version);

            var concurrentQuery = new CoreModel.Selector(CoreModel.SelectorKind.Query,
                query: new(CoreModel.QueryKind.Equal, "System.Title", CoreModel.FieldValue.String("concurrent selector")));
            _transport.SetItem(44, "new native pin target", 1, Actor);
            _transport.BeforeItemReadResponse = async () =>
            {
                var latest = (await store.ReadAsync(connection, default)).Value;
                var bench = latest.Benches.Single();
                var changed = new CoreModel.Bench(bench.Id, bench.Name, bench.Selectors.Add(concurrentQuery));
                (await store.CommitAsync(connection, latest.Version,
                    new CoreModel.StoreSnapshot(latest.Version + 1, latest.Items, latest.Relationships, latest.Drafts,
                        latest.Proposals, latest.Journal, latest.Mappings, [changed]), default)).IsSuccess.ShouldBeTrue();
            };
            (await commands.WorkspaceTrack(44, "json")).ShouldBe(0, stderr.ToString());
            var afterRace = (await store.ReadAsync(connection, default)).Value.Benches.Single();
            afterRace.Selectors.ShouldContain(concurrentQuery);
            afterRace.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Pin
                && selector.Item!.NativeId == "44");
            stdout.GetStringBuilder().Clear();
            (await commands.BenchList("json")).ShouldBe(0, stderr.ToString());
            using var listed = JsonDocument.Parse(stdout.ToString());
            listed.RootElement[0].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
            listed.RootElement[0].GetProperty("id").GetString().ShouldBe(created.Id.ToString("D"));
            _transport.CommittedWrites.ShouldBe(0);
            _transport.PatchAttempts.ShouldBe(0);
            _transport.PostCount.ShouldBe(0);
            final.Drafts.ShouldBeEmpty();
            final.Proposals.ShouldBeEmpty();
            root.GetService<BenchCommand>().ShouldBeNull();
            root.GetService<TrackingCommand>().ShouldBeNull();
            root.GetService<IProviderWorkItemWriter>().ShouldBeNull();
            root.GetService<IPendingChangeStore>().ShouldBeNull();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreActiveContextAndNativeEditorPreserveTargetingAndRealConcurrentFields()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "editor-context-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        var editor = Substitute.For<IEditorLauncher>();
        editor.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult<string?>(call.ArgAt<string>(0).Replace("active scoped edit", "editor scoped edit", StringComparison.Ordinal)));
        services.AddSingleton(editor);
        services.AddTwigWorkspaceServices(paths.StartDir, ["set", "42"]);
        using var root = services.BuildServiceProvider();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.Set("42", "human")).ShouldBe(0, stderr.ToString());
            var selection = (await admitted.Workspace.Context.ReadAsync(default)).Value;
            selection.History[selection.Position].NativeId.ShouldBe("42");
            (await commands.Set("43", "json")).ShouldBe(2);
            (await commands.Edit(output: "json")).ShouldBe(2);
            await editor.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            (await commands.Update("System.Title", "active scoped edit", output: "human")).ShouldBe(0, stderr.ToString());
            _transport.TitleOf(42).ShouldBe("active scoped edit");
            (await commands.Edit(output: "human")).ShouldBe(0, stderr.ToString());
            _transport.TitleOf(42).ShouldBe("editor scoped edit");
            _transport.CommittedWrites.ShouldBe(2);
            editor.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _transport.SetItem(42, "native concurrent field", 100, Actor);
                return Task.FromResult<string?>(call.ArgAt<string>(0).Replace("editor scoped edit", "stale editor field", StringComparison.Ordinal));
            });
            (await commands.Edit(output: "human")).ShouldBe(1, "the editor must merge against its actual activation baseline");
            _transport.TitleOf(42).ShouldBe("native concurrent field");
            _transport.CommittedWrites.ShouldBe(2, "a real conflict must not invoke an old retry writer");
            var captured = (await store.ReadAsync(connection, default)).Value;
            captured.Drafts.Single().Changes["System.Title"].Text.ShouldBe("stale editor field");
            captured.Drafts.Single().Baseline!.Fields["System.Title"].Text.ShouldBe("editor scoped edit");
            captured.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            var wrongInstance = selection with { StoreInstance = Guid.NewGuid() };
            await File.WriteAllTextAsync(Path.Combine(paths.RepoRoot, ".twig", "core-context.json"),
                JsonSerializer.Serialize(wrongInstance, CoreContextJsonContext.Default.CoreContextDocument));
            var reads = _transport.WorkRequests;
            (await commands.Update("System.Title", "must not adopt context", output: "human")).ShouldBe(1);
            _transport.WorkRequests.ShouldBe(reads);
            _transport.CommittedWrites.ShouldBe(2);
            await File.WriteAllTextAsync(Path.Combine(paths.RepoRoot, ".twig", "core-context.json"),
                JsonSerializer.Serialize(selection, CoreContextJsonContext.Default.CoreContextDocument));
            var children = new List<int>();
            var childKeys = new List<Guid>();
            foreach (var title in new[] { "Navigation child one", "Navigation child two" })
            {
                stdout.GetStringBuilder().Clear();
                (await commands.SeedNew(title: title, type: "Task", parent: 42, output: "json")).ShouldBe(0, stderr.ToString());
                using var child = JsonDocument.Parse(stdout.ToString());
                children.Add(child.RootElement.GetProperty("id").GetInt32());
                childKeys.Add(Guid.Parse(child.RootElement.GetProperty("localId").GetString()!));
            }
            (await commands.NavDown(children[0].ToString(CultureInfo.InvariantCulture), "json")).ShouldBe(2);
            (await commands.NavDown(children[0].ToString(CultureInfo.InvariantCulture), "human")).ShouldBe(0, stderr.ToString());
            (await commands.NavNext("human")).ShouldBe(0, stderr.ToString());
            var siblingSelection = (await admitted.Workspace.Context.ReadAsync(default)).Value;
            siblingSelection.History[siblingSelection.Position].LocalId.ShouldBe(childKeys[1]);
            (await commands.NavPrev("human")).ShouldBe(0, stderr.ToString());
            (await commands.NavUp("human")).ShouldBe(0, stderr.ToString());
            (await commands.NavBack("human")).ShouldBe(0, stderr.ToString());
            (await commands.NavFore("human")).ShouldBe(0, stderr.ToString());
            var returned = (await admitted.Workspace.Context.ReadAsync(default)).Value;
            returned.History[returned.Position].NativeId.ShouldBe("42");
            stdout.GetStringBuilder().Clear();
            var beforeHistory = _transport.WorkRequests;
            (await commands.NavHistory(nonInteractive: true, output: "json")).ShouldBe(0, stderr.ToString());
            using var history = JsonDocument.Parse(stdout.ToString());
            history.RootElement.GetArrayLength().ShouldBeGreaterThanOrEqualTo(5);
            _transport.WorkRequests.ShouldBe(beforeHistory);
            _transport.CommittedWrites.ShouldBe(2, "context/navigation must not reach any tracker writer");
            stdout.GetStringBuilder().Clear();
            var beforeTree = _transport.WorkRequests;
            (await commands.Show(42, "json", tree: true)).ShouldBe(0, stderr.ToString());
            using var tree = JsonDocument.Parse(stdout.ToString());
            tree.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
            tree.RootElement.GetArrayLength().ShouldBe(1);
            tree.RootElement[0].GetProperty("children").GetArrayLength().ShouldBe(2);
            tree.RootElement[0].GetProperty("children").EnumerateArray().Select(child => child.GetProperty("id").GetInt32())
                .ShouldBe(children, ignoreOrder: true);
            tree.RootElement[0].GetProperty("children").EnumerateArray().ShouldAllBe(child =>
                child.GetProperty("isSeed").GetBoolean() && child.GetProperty("relationshipOrigin").GetString() == "local");
            _transport.WorkRequests.ShouldBe(beforeTree, "cached tree detail must not fetch work items implicitly");
            stdout.GetStringBuilder().Clear();
            (await commands.Tree(42, "json", depth: 0)).ShouldBe(0, stderr.ToString());
            using var shallow = JsonDocument.Parse(stdout.ToString());
            shallow.RootElement[0].TryGetProperty("children", out _).ShouldBeFalse();
            stdout.GetStringBuilder().Clear();
            (await commands.Query(title: "NATIVE CONCURRENT", output: "json")).ShouldBe(0, stderr.ToString());
            using var query = JsonDocument.Parse(stdout.ToString());
            query.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
            query.RootElement.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(42);
            query.RootElement.GetProperty("items")[0].GetProperty("title").GetString().ShouldBe("native concurrent field");
            _transport.CommittedWrites.ShouldBe(2, "query must never flush the retained concurrent draft");
            (await store.ReadAsync(connection, default)).Value.Drafts.ShouldContain(draft =>
                !draft.Key.IsLocal && draft.Changes["System.Title"].Text == "stale editor field");
            root.GetService<EditCommand>().ShouldBeNull();
            root.GetService<SetCommand>().ShouldBeNull();
            root.GetService<NavigationCommands>().ShouldBeNull();
            root.GetService<NavigationHistoryCommands>().ShouldBeNull();
            root.GetService<QueryCommand>().ShouldBeNull();
            root.GetService<IProviderWorkItemWriter>().ShouldBeNull();
            root.GetService<IPendingChangeStore>().ShouldBeNull();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreQueryFindsLaterPageMatchesAndPreservesUnpublishedDraftsWithoutWriters()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        for (var id = 1000; id <= 1205; id++)
            _transport.SetItem(id, id == 1205 ? "later page match" : "not selected", 1, Actor);
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "paged-query-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        var initialized = await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default);
        initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
        var key = CoreModel.ItemKey.Local(Guid.NewGuid());
        var fields = new CoreModel.FieldValues([
            KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("unpublished draft")),
            KeyValuePair.Create("System.State", CoreModel.FieldValue.String("To Do")),
        ]);
        (await store.CommitAsync(connection, 0,
            new CoreModel.StoreSnapshot(1, drafts: [new(key, "Task", null, fields)]),
            default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["query", "--title", "LATER PAGE", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.Query(title: "LATER PAGE", output: "json", top: 1)).ShouldBe(0, stderr.ToString());
            using var result = JsonDocument.Parse(stdout.ToString());
            result.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
            result.RootElement.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(1205);
            _transport.QueryAfterIds.Count.ShouldBeGreaterThanOrEqualTo(2);
            _transport.QueryAfterIds[0].ShouldBe(0);
            _transport.QueryAfterIds[1].ShouldBeGreaterThan(0);
            _transport.CommittedWrites.ShouldBe(0);
            _transport.PostCount.ShouldBe(0);
            var retained = (await store.ReadAsync(connection, default)).Value;
            retained.Drafts.Single().Key.ShouldBe(key);
            retained.Drafts.Single().Changes.ShouldBe(fields);
            retained.Items.ShouldContain(item => item.Key.NativeId == "1205"
                && item.RelationshipsComplete && item.ResourcesComplete);
            retained.Journal.ShouldBeEmpty();
            root.GetService<QueryCommand>().ShouldBeNull();
            root.GetService<IProviderWorkItemWriter>().ShouldBeNull();
            root.GetService<IPendingChangeStore>().ShouldBeNull();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreChainAuthoringValidationAndReconciliationRetainOriginalCreationReferences()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "chain-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        var editor = Substitute.For<IEditorLauncher>();
        editor.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<string?>(call.ArgAt<string>(0).Replace("build locally updated", "build editor updated", StringComparison.Ordinal)));
        services.AddSingleton(editor);
        services.AddTwigWorkspaceServices(paths.StartDir, ["seed", "chain", "--parent", "42", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.SeedChain("design,build,verify", 42, "Task", "json")).ShouldBe(0, stderr.ToString());
            using var chain = JsonDocument.Parse(stdout.ToString());
            chain.RootElement.GetProperty("count").GetInt32().ShouldBe(3);
            var staged = (await store.ReadAsync(connection, default)).Value;
            staged.Drafts.Length.ShouldBe(3);
            var first = staged.Drafts.Single(draft => draft.Links.IsEmpty);
            var second = staged.Drafts.Single(draft => draft.Links.Any(link => link.Other == first.Key));
            var third = staged.Drafts.Single(draft => draft.Links.Any(link => link.Other == second.Key));
            _transport.PostCount.ShouldBe(0);
            var secondAlias = chain.RootElement.GetProperty("seeds").EnumerateArray()
                .Single(row => row.GetProperty("localId").GetString() == second.Key.LocalId.ToString("D")).GetProperty("id").GetInt32();
            (await commands.Update("System.Title", "build locally updated", id: secondAlias, output: "human"))
                .ShouldBe(0, stderr.ToString());
            stdout.GetStringBuilder().Clear();
            (await commands.SeedEdit(secondAlias, "json")).ShouldBe(0, stderr.ToString());
            var edited = (await store.ReadAsync(connection, default)).Value.Drafts.Single(draft => draft.Key == second.Key);
            edited.Changes["System.Title"].Text.ShouldBe("build editor updated");
            edited.Links.ShouldBe(second.Links);
            edited.Parent.ShouldBe(second.Parent);
            edited.Changes.TryGetValue("System.IterationPath", out _).ShouldBeFalse("untouched missing values must not become null/empty fields");
            _transport.PostCount.ShouldBe(0);
            editor.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<string?>(call.ArgAt<string>(0) + "\n# Title\nduplicate field"));
            (await commands.SeedEdit(secondAlias, "human")).ShouldBe(1, "duplicate editor sections must not silently overwrite a field");
            (await store.ReadAsync(connection, default)).Value.Drafts.Single(draft => draft.Key == second.Key).Changes.ShouldBe(edited.Changes);
            editor.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<string?>>(async call =>
            {
                var current = (await admitted.Workspace.SnapshotAsync(default)).Value;
                var capture = current.Drafts.Single(draft => draft.Key == second.Key);
                (await admitted.Workspace.CaptureSeedFieldsAsync(capture, current.Version,
                    new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("build concurrent edit"))]), default))
                    .IsSuccess.ShouldBeTrue();
                return call.ArgAt<string>(0).Replace("build editor updated", "stale editor overwrite", StringComparison.Ordinal);
            });
            (await commands.SeedEdit(secondAlias, "human")).ShouldBe(1, "the editor must use its exact activation capture, not overwrite concurrent draft intent");
            (await store.ReadAsync(connection, default)).Value.Drafts.Single(draft => draft.Key == second.Key)
                .Changes["System.Title"].Text.ShouldBe("build concurrent edit");
            stdout.GetStringBuilder().Clear();
            (await commands.SeedValidate(output: "json")).ShouldBe(0, stderr.ToString());
            using var validation = JsonDocument.Parse(stdout.ToString());
            validation.RootElement.GetProperty("passed").GetInt32().ShouldBe(3);
            _transport.PostCount.ShouldBe(0);
            var preview = (await admitted.Workspace.PreviewDraftAsync(first.Key, default)).Value;
            var applied = await admitted.Workspace.ApplyAsync(preview.Proposal!, preview.Proposal!.Digest, default);
            applied.IsSuccess.ShouldBeTrue();
            applied.Value.EffectsVerified.ShouldBeTrue();
            var beforeSettlement = (await store.ReadAsync(connection, default)).Value;
            beforeSettlement.Drafts.Length.ShouldBe(3, "direct journal execution deliberately leaves captured drafts for reconciliation");
            beforeSettlement.Mappings.ShouldHaveSingleItem().Local.ShouldBe(first.Key);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedReconcile("json")).ShouldBe(0, stderr.ToString());
            using var reconciliation = JsonDocument.Parse(stdout.ToString());
            reconciliation.RootElement.GetProperty("seedsSettled").GetInt32().ShouldBe(1);
            var reconciled = (await store.ReadAsync(connection, default)).Value;
            reconciled.Drafts.Length.ShouldBe(2);
            reconciled.Drafts.Single(draft => draft.Key == second.Key).Links.Single().Other.ShouldBe(first.Key);
            _transport.PostCount.ShouldBe(1, "reconciliation must never replay creation or dispatch a later planned operation");
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(all: true, output: "human")).ShouldBe(0, stderr.ToString());
            var complete = (await store.ReadAsync(connection, default)).Value;
            complete.Drafts.ShouldBeEmpty();
            complete.Mappings.Length.ShouldBe(3);
            complete.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            complete.Proposals.Single(proposal => proposal.Operations.Length == 2).Operations[0].Links.Single().Other.ShouldBe(first.Key);
            _transport.PostCount.ShouldBe(3);
            _transport.PatchAttempts.ShouldBe(0);
            root.GetService<SeedChainCommand>().ShouldBeNull();
            root.GetService<SeedLinkRepairCommand>().ShouldBeNull();
            root.GetService<SeedValidateCommand>().ShouldBeNull();
            root.GetService<SeedEditCommand>().ShouldBeNull();
            complete.Mappings.ShouldContain(mapping => mapping.Local == third.Key);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreParentedSeedPublishesItsHierarchyInTheCreationCallWithoutAPostCreateWriter()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "parented-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["seed", "new", "--parent", "42", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.SeedNew(title: "Parented rooted seed", type: "Task", parent: 42, output: "json"))
                .ShouldBe(0, stderr.ToString());
            using var created = JsonDocument.Parse(stdout.ToString());
            var alias = created.RootElement.GetProperty("id").GetInt32();
            _transport.CommittedWrites.ShouldBe(0);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(alias, linkBranch: "feature/rooted", repo: "FixtureRepo", output: "human")).ShouldBe(0, stderr.ToString());
            _transport.PostCount.ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(0, "parenting must be in the creation call, not a reachable post-create writer");
            var snapshot = (await store.ReadAsync(connection, default)).Value;
            snapshot.Drafts.ShouldBeEmpty();
            snapshot.Journal.ShouldHaveSingleItem().Status.ShouldBe(CoreModel.OpStatus.Verified);
            var mapping = snapshot.Mappings.Single();
            snapshot.Items.Single(item => item.Key == mapping.Remote).Resources.ShouldContain(new CoreModel.ResourceLink("ArtifactLink",
                "vstfs:///Git/Ref/22222222-2222-2222-2222-222222222222/44444444-4444-4444-4444-444444444444/GBfeature%2Frooted", "Branch"));
            snapshot.Relationships.ShouldContain(new CoreModel.Relationship(
                CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"),
                mapping.Remote, "Native.ParentForward", CoreModel.RelationshipOrigin.Provider));
            root.GetService<IProviderWorkItemWriter>().ShouldBeNull();
            stdout.GetStringBuilder().Clear();
            (await commands.SeedNew(title: "Local publication parent", type: "Task", noParent: true, output: "json")).ShouldBe(0);
            using var localParent = JsonDocument.Parse(stdout.ToString());
            var parentAlias = localParent.RootElement.GetProperty("id").GetInt32();
            var parentUuid = Guid.Parse(localParent.RootElement.GetProperty("localId").GetString()!);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedNew(title: "Local publication child", type: "Task", parent: parentAlias, output: "json")).ShouldBe(0);
            using var localChild = JsonDocument.Parse(stdout.ToString());
            var childUuid = Guid.Parse(localChild.RootElement.GetProperty("localId").GetString()!);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(all: true, output: "human")).ShouldBe(0, stderr.ToString());
            var complete = (await store.ReadAsync(connection, default)).Value;
            complete.Drafts.ShouldBeEmpty();
            var parentMapping = complete.Mappings.Single(value => value.Local.LocalId == parentUuid);
            var childMapping = complete.Mappings.Single(value => value.Local.LocalId == childUuid);
            complete.Relationships.ShouldContain(new CoreModel.Relationship(parentMapping.Remote, childMapping.Remote,
                "Native.ParentForward", CoreModel.RelationshipOrigin.Provider));
            var ordered = complete.Proposals.Single(value => value.Operations.Length == 2);
            ordered.Operations[1].Other.ShouldBe(parentMapping.Local);
            complete.Journal.Where(row => row.Digest == ordered.Digest).ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            _transport.PostCount.ShouldBe(3);
            _transport.PatchAttempts.ShouldBe(0);
            var aliases = new List<int>();
            var uuids = new List<Guid>();
            foreach (var title in new[] { "Design linked chain", "Build linked chain", "Review linked chain" })
            {
                stdout.GetStringBuilder().Clear();
                (await commands.SeedNew(title: title, type: "Task", noParent: true, output: "json")).ShouldBe(0);
                using var linked = JsonDocument.Parse(stdout.ToString());
                aliases.Add(linked.RootElement.GetProperty("id").GetInt32());
                uuids.Add(Guid.Parse(linked.RootElement.GetProperty("localId").GetString()!));
            }
            (await commands.SeedLink(aliases[0], aliases[1], "blocks", "human")).ShouldBe(0, stderr.ToString());
            (await commands.SeedLink(aliases[1], aliases[2], "blocks", "human")).ShouldBe(0, stderr.ToString());
            (await commands.SeedLink(aliases[2], aliases[0], "blocks", "human")).ShouldBe(1, "a cyclic creation graph must not be captured");
            (await commands.SeedLink(aliases[2], 42, "related", "human")).ShouldBe(0, stderr.ToString());
            (await commands.SeedUnlink(aliases[2], 42, "related", "human")).ShouldBe(0, stderr.ToString());
            (await commands.SeedLink(aliases[2], 42, "related", "human")).ShouldBe(0, stderr.ToString());
            stdout.GetStringBuilder().Clear();
            (await commands.SeedLinks(output: "json")).ShouldBe(0, stderr.ToString());
            using var capturedLinks = JsonDocument.Parse(stdout.ToString());
            capturedLinks.RootElement.GetProperty("count").GetInt32().ShouldBe(3);
            _transport.PostCount.ShouldBe(3, "link capture must not dispatch native work");
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(all: true, linkBranch: "feature/rooted", repo: "FixtureRepo", output: "human"))
                .ShouldBe(0, stderr.ToString());
            var linkedComplete = (await store.ReadAsync(connection, default)).Value;
            linkedComplete.Drafts.ShouldBeEmpty();
            var chain = linkedComplete.Proposals.Single(proposal => proposal.Operations.Length == 3);
            chain.Operations[1].Links.Single().Other.LocalId.ShouldBe(uuids[0]);
            chain.Operations[2].Links.ShouldContain(link => link.Other.LocalId == uuids[1]);
            var linkedMappings = uuids.Select(uuid => linkedComplete.Mappings.Single(mapping => mapping.Local.LocalId == uuid)).ToArray();
            linkedComplete.Relationships.ShouldContain(new CoreModel.Relationship(linkedMappings[0].Remote, linkedMappings[1].Remote,
                "Native.DependencyForward", CoreModel.RelationshipOrigin.Provider));
            linkedComplete.Relationships.ShouldContain(new CoreModel.Relationship(linkedMappings[1].Remote, linkedMappings[2].Remote,
                "Native.DependencyForward", CoreModel.RelationshipOrigin.Provider));
            linkedComplete.Journal.Where(row => row.Digest == chain.Digest).ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            _transport.PostCount.ShouldBe(6);
            _transport.PatchAttempts.ShouldBe(0);
            root.GetService<SeedLinkCommand>().ShouldBeNull();
            root.GetService<SeedPublishCommand>().ShouldBeNull();
            root.GetService<IPendingChangeStore>().ShouldBeNull();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreSeedPublicationUsesTheRetainedUuidAndPermanentVerifiedMapping()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "seed-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["seed", "view", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        root.GetService<SeedPublishCommand>().ShouldBeNull();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var commands = new TwigCommands(root);
            (await commands.SeedNew(title: "Rooted creation", type: "Task", noParent: true, output: "json"))
                .ShouldBe(0, stderr.ToString());
            var seed = (await store.ReadAsync(connection, default)).Value.Drafts.ShouldHaveSingleItem();
            seed.Key.LocalId.Version.ShouldBe(7);
            seed.Changes["System.State"].Text.ShouldBe("To Do");
            seed.Changes["System.AssignedTo"].Text.ShouldBe(Actor);
            _transport.PostCount.ShouldBe(0);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedView("json")).ShouldBe(0, stderr.ToString());
            using var view = JsonDocument.Parse(stdout.ToString());
            var alias = view.RootElement[0].GetProperty("id").GetInt32();
            alias.ShouldBeLessThan(0);
            view.RootElement[0].GetProperty("localId").GetString().ShouldBe(seed.Key.LocalId.ToString("D"));
            stdout.GetStringBuilder().Clear();
            var requestsBeforeLocalRead = _transport.TotalRequests;
            (await commands.Show(output: "json", localId: seed.Key.LocalId.ToString("D"))).ShouldBe(0);
            using var localRead = JsonDocument.Parse(stdout.ToString());
            localRead.RootElement.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
            localRead.RootElement.GetProperty("revision").ValueKind.ShouldBe(JsonValueKind.Null);
            _transport.TotalRequests.ShouldBe(requestsBeforeLocalRead);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(alias, output: "json")).ShouldBe(2);
            _transport.PostCount.ShouldBe(0);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(alias, output: "human")).ShouldBe(0, stderr.ToString());
            var final = (await store.ReadAsync(connection, default)).Value;
            final.Drafts.ShouldBeEmpty();
            var mapping = final.Mappings.ShouldHaveSingleItem();
            mapping.Local.ShouldBe(seed.Key);
            mapping.Remote.NativeId.ShouldBe("45");
            final.Journal.ShouldHaveSingleItem().Status.ShouldBe(CoreModel.OpStatus.Verified);
            final.Proposals.ShouldHaveSingleItem().Operations.ShouldHaveSingleItem().Item.ShouldBe(seed.Key);
            _transport.PostCount.ShouldBe(1);
            stdout.GetStringBuilder().Clear();
            var requestsBeforeMappedRead = _transport.TotalRequests;
            (await commands.Show(alias, output: "json")).ShouldBe(0);
            using var mappedRead = JsonDocument.Parse(stdout.ToString());
            mappedRead.RootElement.GetProperty("id").GetInt32().ShouldBe(45);
            mappedRead.RootElement.GetProperty("localId").GetString().ShouldBe(seed.Key.LocalId.ToString("D"));
            mappedRead.RootElement.GetProperty("isSeed").GetBoolean().ShouldBeFalse();
            _transport.TotalRequests.ShouldBe(requestsBeforeMappedRead);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(alias, output: "human")).ShouldBe(1);
            _transport.PostCount.ShouldBe(1, "the publication UUID must not be recreated after permanent mapping");
            var unknownSeed = (await admitted.Workspace.StageSeedAsync("Fixture.Task",
                new([
                    KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Unacknowledged rooted creation")),
                    KeyValuePair.Create("System.State", CoreModel.FieldValue.String("To Do")),
                ]), default, Guid.CreateVersion7())).Value;
            stdout.GetStringBuilder().Clear();
            (await commands.SeedView("json")).ShouldBe(0);
            using var unknownView = JsonDocument.Parse(stdout.ToString());
            var unknownAlias = unknownView.RootElement[0].GetProperty("id").GetInt32();
            _transport.AfterCommit = () => throw new IOException("Fixture lost create response after native commit");
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(unknownAlias, output: "human")).ShouldBe(1);
            var unknown = (await store.ReadAsync(connection, default)).Value;
            unknown.Drafts.ShouldHaveSingleItem().Key.ShouldBe(unknownSeed.Key);
            unknown.Mappings.Length.ShouldBe(1, "unknown create must not guess the native identity");
            unknown.Journal.ShouldContain(row => row.Status == CoreModel.OpStatus.Indeterminate);
            _transport.PostCount.ShouldBe(2);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedPublish(unknownAlias, output: "human")).ShouldBe(1);
            _transport.PostCount.ShouldBe(2, "the unresolved UUID must fence replacement proposals, not only the old digest");
            var requestsBeforeUnknownDiscard = _transport.TotalRequests;
            (await commands.SeedReconcile("json")).ShouldBe(1);
            _transport.PostCount.ShouldBe(2, "reconciliation cannot guess or replay the unknown creation UUID");
            _transport.TotalRequests.ShouldBe(requestsBeforeUnknownDiscard);
            (await commands.SeedDiscard(unknownAlias, yes: true, output: "json")).ShouldBe(1);
            (await store.ReadAsync(connection, default)).Value.Drafts.ShouldHaveSingleItem().Key.ShouldBe(unknownSeed.Key);
            _transport.TotalRequests.ShouldBe(requestsBeforeUnknownDiscard, "unknown seed abandonment must refuse locally without transport");
            stdout.GetStringBuilder().Clear();
            (await commands.SeedNew(title: "Unattempted discard", type: "Task", noParent: true, output: "json")).ShouldBe(0);
            using var disposableSeed = JsonDocument.Parse(stdout.ToString());
            var discardAlias = disposableSeed.RootElement.GetProperty("id").GetInt32();
            stdout.GetStringBuilder().Clear();
            (await commands.SeedDiscard(discardAlias, yes: false, output: "json")).ShouldBe(2);
            (await store.ReadAsync(connection, default)).Value.Drafts.Length.ShouldBe(2);
            stdout.GetStringBuilder().Clear();
            (await commands.SeedDiscard(discardAlias, yes: true, output: "json")).ShouldBe(0);
            var afterDiscard = (await store.ReadAsync(connection, default)).Value;
            afterDiscard.Drafts.ShouldHaveSingleItem().Key.ShouldBe(unknownSeed.Key);
            afterDiscard.Journal.Length.ShouldBe(unknown.Journal.Length);
            afterDiscard.Mappings.Length.ShouldBe(1);
            _transport.PostCount.ShouldBe(2);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreSaveUsesDraftJournalInsteadOfResolvingALegacyPendingFlusher()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "save-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["save", "--all", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        root.GetService<IPendingChangeFlusher>().ShouldBeNull();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        (await admitted.Workspace.StageEditAsync(key,
            new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Explicit saved draft"))]), default)).IsSuccess.ShouldBeTrue();
        (await store.ReadAsync(connection, default)).Value.Drafts.ShouldHaveSingleItem();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            (await new TwigCommands(root).Save(all: true, output: "json")).ShouldBe(0, stderr.ToString());
            var saved = (await store.ReadAsync(connection, default)).Value;
            saved.Drafts.ShouldBeEmpty();
            saved.Journal.ShouldHaveSingleItem().Status.ShouldBe(CoreModel.OpStatus.Verified);
            _transport.CommittedWrites.ShouldBe(1);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreInitCliCreatesAnAdmittedWorkspaceAndRefusesReinitializationWithoutChangingIt()
    {
        await AttachAsync();
        var root = Path.Combine(_temp, "fresh-core-checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        _transport.CoreMetadataEnabled = true;
        var database = Path.Combine(root, "explicit-core.db");
        var arguments = new[] { "init", "--org", "fixture", "--project", "Project",
            "--core-store", database, "-o", "json" };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int Run() => Twig.TwigConsoleApp.Run(arguments, services =>
        {
            services.AddSingleton<IConnectionBindingService>(_bindings);
            services.AddSingleton(_http);
            services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
            services.AddTwigWorkspaceServices(root, arguments, Path.Combine(Home, "display.json"));
            services.AddSingleton<ISystemWorktreeRegistry>(_systemRegistry);
        });
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Run().ShouldBe(0, stderr.ToString());
            using var output = JsonDocument.Parse(stdout.ToString());
            output.RootElement.GetProperty("coreInitialized").GetBoolean().ShouldBeTrue();
            await using var marker = File.OpenRead(Path.Combine(root, ".twig", CoreWorkspaceFactory.FileName));
            var document = (await JsonSerializer.DeserializeAsync(marker,
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument))!;
            document.PrincipalId.ShouldBe(Actor);
            document.DatabasePath.ShouldBe(database);
            var store = (await CleanStore.OpenAsync(database, expectedInstance: document.DatabaseInstance)).Value;
            (await store.CheckWorkspaceAsync(document.Connection)).IsSuccess.ShouldBeTrue();
            var snapshot = (await store.ReadAsync(document.Connection, default)).Value;
            snapshot.Journal.ShouldBeEmpty();
            snapshot.Drafts.ShouldBeEmpty();
            var bytes = await File.ReadAllBytesAsync(database);
            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Run().ShouldBe(1);
            using var refusal = JsonDocument.Parse(stderr.ToString());
            refusal.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("existing");
            (await File.ReadAllBytesAsync(database)).ShouldBe(bytes);
            _transport.CommittedWrites.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreInitializationDerivesNativeIdentityAndNeverReusesAnExistingDatabase()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var database = Path.Combine(paths.TwigDir, "explicit-init-core.db");
        var initialized = await CoreWorkspaceInitializer.InitializeAsync(config, paths, _bindings,
            database, default, new CoreTransportOverride(_transport.NewHandle));
        initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
        initialized.Value.ShouldBeTrue();
        await using var marker = File.OpenRead(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName));
        var document = (await JsonSerializer.DeserializeAsync(marker, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument))!;
        document.Connection.ResourceScope.ShouldBe("11111111-1111-1111-1111-111111111111");
        document.Connection.Destination.ShouldBe("22222222-2222-2222-2222-222222222222");
        document.PrincipalId.ShouldBe(Actor);
        var store = (await CleanStore.OpenAsync(database, expectedInstance: document.DatabaseInstance)).Value;
        (await store.CheckWorkspaceAsync(document.Connection)).IsSuccess.ShouldBeTrue();
        var bytes = await File.ReadAllBytesAsync(database);
        (await CoreWorkspaceInitializer.InitializeAsync(config, paths, _bindings, database, default,
            new CoreTransportOverride(_transport.NewHandle))).IsSuccess.ShouldBeFalse();
        (await File.ReadAllBytesAsync(database)).ShouldBe(bytes);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task LegacyWorkspaceMutationIsRefusedBeforeProductionWriterRegistration()
    {
        var legacy = await AttachAsync();
        var paths = legacy.GetRequiredService<TwigPaths>();
        var requests = _transport.WorkRequests;
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        var failure = Should.Throw<InvalidOperationException>(() =>
            services.AddTwigWorkspaceServices(paths.StartDir, ["update", "System.Title", "not admitted", "--id", "42", "-o", "json"]));
        failure.Message.ShouldContain("Core workspace");
        _transport.WorkRequests.ShouldBe(requests);
        _transport.CommittedWrites.ShouldBe(0);
        services.Any(s => s.ServiceType == typeof(IAdoWorkItemService)).ShouldBeFalse();
        services.Any(s => s.ServiceType == typeof(IProviderWorkItemWriter)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExplicitCoreProductionRootReadsAndUpdatesThroughDraftDigestJournalWithoutLegacyWriters()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "production-core.db");
        var created = await CleanStore.CreateAsync(database);
        created.IsSuccess.ShouldBeTrue();
        var store = created.Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var arguments = new[] { "update", "System.Title", "Core production title", "--id", "42", "-o", "json" };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = Twig.TwigConsoleApp.Run(arguments, services =>
            {
                services.AddSingleton<IConnectionBindingService>(_bindings);
                services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                services.AddTwigWorkspaceServices(paths.StartDir, arguments, Path.Combine(Home, "display.json"));
            });
            exit.ShouldBe(0, stderr.ToString());
            using var output = JsonDocument.Parse(stdout.ToString());
            output.RootElement.GetProperty("digest").GetString().ShouldNotBeNullOrWhiteSpace();
            output.RootElement.GetProperty("journalStatus").GetString().ShouldBe("Verified");
            _transport.CommittedWrites.ShouldBe(1);
            var snapshot = (await store.ReadAsync(connection, default)).Value;
            snapshot.Drafts.ShouldBeEmpty();
            snapshot.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Verified);
            snapshot.Proposals.Single().Digest.ShouldBe(output.RootElement.GetProperty("digest").GetString());
            var check = new ServiceCollection();
            check.AddSingleton<IConnectionBindingService>(_bindings);
            check.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
            check.AddTwigWorkspaceServices(paths.StartDir, ["show", "42", "-o", "json"], Path.Combine(Home, "display.json"));
            using var root = check.BuildServiceProvider();
            root.GetService<IProviderWorkItemWriter>().ShouldBeNull();
            root.GetService<IAdoWorkItemService>().ShouldBeNull();
            root.GetService<IPendingChangeStore>().ShouldBeNull();
            stdout.GetStringBuilder().Clear();
            var show = new TwigCommands(root);
            var requestsBeforeCachedRead = _transport.TotalRequests;
            (await show.Show(42, "json")).ShouldBe(0, stderr.ToString());
            using var read = JsonDocument.Parse(stdout.ToString());
            read.RootElement.GetProperty("title").GetString().ShouldBe("Core production title");
            read.RootElement.GetProperty("cached").GetBoolean().ShouldBeTrue();
            _transport.TotalRequests.ShouldBe(requestsBeforeCachedRead, "a cached show must not acquire schema or work HTTP");
            _transport.CommittedWrites.ShouldBe(1, "show must not flush unrelated pending state");
            using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
            var workspace = admitted.Workspace;
            var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
            (await workspace.StageEditAsync(key,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("File-reviewed production title"))]), default))
                .IsSuccess.ShouldBeTrue();
            var preview = (await workspace.PreviewDraftAsync(key, default)).Value;
            var proposal = preview.Proposal.ShouldNotBeNull();
            var proposalPath = Path.Combine(paths.TwigDir, "core-reviewed-proposal.json");
            await File.WriteAllBytesAsync(proposalPath, CoreModel.Core.ProposalBytes(proposal));
            var workReads = _transport.WorkRequests;
            stdout.GetStringBuilder().Clear();
            (await show.PlanApply(proposalPath, new string('0', 64), "json")).ShouldBe(1);
            _transport.WorkRequests.ShouldBe(workReads, "wrong digest must refuse before native context or work HTTP");
            stdout.GetStringBuilder().Clear();
            (await show.PlanApply(proposalPath, proposal.Digest, "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(2);
            (await store.ReadAsync(connection, default)).Value.Drafts.ShouldBeEmpty();
            stdout.GetStringBuilder().Clear();
            (await show.PlanApply(proposalPath, proposal.Digest, "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(2, "Verified file reapply must not dispatch");
            stdout.GetStringBuilder().Clear();
            (await show.Note(text: "Core root note", output: "json", id: 42, format: "raw")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(3);
            stdout.GetStringBuilder().Clear();
            (await show.LinkArtifact("https://example.test/root-evidence", "Root evidence", 42, "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(4);
            var final = (await store.ReadAsync(connection, default)).Value;
            final.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            final.Items.Single().Resources.ShouldContain(resource => resource.Target == "https://example.test/root-evidence");
            root.GetService<IWorkItemDiscussionWriter>().ShouldBeNull();
            stdout.GetStringBuilder().Clear();
            (await show.State("Doing", "json", 42)).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(5);
            (await store.ReadAsync(connection, default)).Value.Items.Single().Fields["System.State"].Text.ShouldBe("Doing");
            stdout.GetStringBuilder().Clear();
            (await show.Batch(state: "Done", set: ["System.Title=Atomic rooted batch"], note: "Reviewed batch note",
                id: 42, output: "json", format: "raw")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(6, "batch fields/state/note must share one revision-tested native call");
            var batchSnapshot = (await store.ReadAsync(connection, default)).Value;
            batchSnapshot.Items.Single().Fields["System.State"].Text.ShouldBe("Done");
            batchSnapshot.Items.Single().Fields["System.History"].Text.ShouldBe("Reviewed batch note");
            stdout.GetStringBuilder().Clear();
            (await show.Patch(42, """{"System.Title":"Typed rooted patch"}""", output: "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(7);
            (await store.ReadAsync(connection, default)).Value.Items.Single().Fields["System.Title"].Text.ShouldBe("Typed rooted patch");
            var otherKey = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "43");
            (await workspace.StageEditAsync(key,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Saved first draft"))]), default)).IsSuccess.ShouldBeTrue();
            (await workspace.StageEditAsync(otherKey,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Saved second draft"))]), default)).IsSuccess.ShouldBeTrue();
            var seed = (await workspace.StageSeedAsync("Fixture.Task",
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Unpublished separate seed"))]), default)).Value;
            stdout.GetStringBuilder().Clear();
            (await show.Save(all: true, output: "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(9);
            var saved = (await store.ReadAsync(connection, default)).Value;
            saved.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seed.Key);
            var multi = saved.Proposals.Single(intent => intent.Operations.Length == 2);
            saved.Journal.Where(row => row.Digest == multi.Digest).ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            _transport.TitleOf(42).ShouldBe("Saved first draft");
            _transport.TitleOf(43).ShouldBe("Saved second draft");
            stdout.GetStringBuilder().Clear();
            var beforeBatchRead = _transport.TotalRequests;
            (await show.ShowBatch("42,43", output: "json", includeFields: true)).ShouldBe(0, stderr.ToString());
            using var batchRead = JsonDocument.Parse(stdout.ToString());
            batchRead.RootElement.GetProperty("items").GetArrayLength().ShouldBe(2);
            batchRead.RootElement.GetProperty("missingIds").GetArrayLength().ShouldBe(0);
            _transport.TotalRequests.ShouldBe(beforeBatchRead);
            stdout.GetStringBuilder().Clear();
            (await show.ShowBatch("42,99999", output: "json")).ShouldBe(1);
            using var missingRead = JsonDocument.Parse(stdout.ToString());
            missingRead.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
            missingRead.RootElement.GetProperty("missingIds")[0].GetInt32().ShouldBe(99999);
            _transport.TotalRequests.ShouldBe(beforeBatchRead, "missing cached items must not silently trigger network reads");
            (await workspace.StageEditAsync(key,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Never publish discarded intent"))]), default)).IsSuccess.ShouldBeTrue();
            var beforeDiscard = (await workspace.SnapshotAsync(default)).Value;
            var captured = beforeDiscard.Drafts.Where(draft => !draft.Key.IsLocal).ToArray();
            (await workspace.StageEditAsync(key,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Later local intent"))]), default)).IsSuccess.ShouldBeTrue();
            (await workspace.DiscardDraftsAsync(captured, beforeDiscard.Version, default)).IsSuccess.ShouldBeFalse();
            stdout.GetStringBuilder().Clear();
            (await show.Discard(42, yes: false, output: "json")).ShouldBe(2);
            (await store.ReadAsync(connection, default)).Value.Drafts.Length.ShouldBe(2);
            stdout.GetStringBuilder().Clear();
            (await show.Discard(42, yes: true, output: "json")).ShouldBe(0, stderr.ToString());
            (await store.ReadAsync(connection, default)).Value.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seed.Key);
            var readsBeforeDelete = _transport.WorkRequests;
            (await show.Delete(42, force: true, output: "json")).ShouldBe(1);
            _transport.WorkRequests.ShouldBe(readsBeforeDelete);
            _transport.CommittedWrites.ShouldBe(9, "discard and unsupported delete must never dispatch");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CleanCoreAdmissionRetainsRealNativeLeaseAndExactDurableAttemptAttribution()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var fields = new CoreModel.FieldValues([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Core reviewed title"))]);
        var proposal = new CoreModel.Proposal(connection, "native-schema", 0,
            [new("edit", CoreModel.OpKind.Batch, key, new(key, "ado-rev:1"), fields)]);
        var created = await CleanStore.CreateAsync(Path.Combine(paths.TwigDir, "isolated-core.db"));
        created.IsSuccess.ShouldBeTrue();
        var store = created.Value;
        var planned = CoreModel.JournalEntry.Planned(proposal.Digest, 0);
        (await store.CommitAsync(connection, 0, new(1, proposals: [proposal], journal: [planned]), default)).IsSuccess.ShouldBeTrue();
        var access = new CleanCoreAdoAccess(runtime.GetRequiredService<IAuthenticationProvider>(), _bindings,
            config, paths, origin, connection, Actor, store);
        var acquired = await access.AcquireAsync(connection, default);
        acquired.IsSuccess.ShouldBeTrue(acquired.IsSuccess ? "" : acquired.Error.Message);
        using var session = acquired.Value;
        var attempt = Guid.NewGuid();
        var intent = new Twig.Providers.Ado.AdoWriteIntent(proposal, 0, attempt,
            new Uri("https://dev.azure.com/fixture/" + connection.Destination + "/_apis/wit/workitems/42?api-version=7.1"),
            "PATCH", "application/json-patch+json",
            """[{"op":"test","path":"/rev","value":1},{"op":"add","path":"/fields/System.Title","value":"Core reviewed title"}]""");
        (await session.AdmitAsync(intent, default)).IsSuccess.ShouldBeFalse("Planned is not durable Applying");
        (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint)).Value.ShouldBeEmpty();
        var applying = planned.Begin(attempt).Value;
        (await store.CommitAsync(connection, 1, new(2, proposals: [proposal], journal: [applying]), default)).IsSuccess.ShouldBeTrue();
        (await session.AdmitAsync(intent with { Attempt = Guid.NewGuid() }, default)).IsSuccess.ShouldBeFalse();
        (await session.AdmitAsync(intent with { Payload = intent.Payload.Replace("\"value\":1", "\"value\":2", StringComparison.Ordinal) }, default))
            .IsSuccess.ShouldBeFalse();
        (await session.AdmitAsync(intent with { Target = new Uri(intent.Target.AbsoluteUri + "&bypassRules=true") }, default))
            .IsSuccess.ShouldBeFalse();
        var admitted = await session.AdmitAsync(intent, default);
        admitted.IsSuccess.ShouldBeTrue(admitted.IsSuccess ? "" : admitted.Error.Message);
        using var admission = admitted.Value;
        var recorded = (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint)).Value.ShouldHaveSingleItem();
        recorded.Intent.Request.CoreCorrelation.ShouldNotBeNull().ProposalDigest.ShouldBe(proposal.Digest);
        recorded.Intent.Request.CoreCorrelation!.Operation.ShouldBe(0);
        recorded.Intent.Request.CoreCorrelation.Attempt.ShouldBe(attempt);
        recorded.Intent.Request.CoreCorrelation.StoreInstance.ShouldBe(store.InstanceId);
        recorded.Intent.Request.Payload.ShouldBe(intent.Payload);
        recorded.Intent.Origin.ShouldBe(origin);
        recorded.Receipt.ShouldBeNull();
        _transport.CommittedWrites.ShouldBe(0, "native admission does not dispatch");
        using var request = new HttpRequestMessage(HttpMethod.Patch, intent.Target);
        request.Headers.Authorization = session.Authorization;
        request.Content = new StringContent(intent.Payload, Encoding.UTF8, intent.MediaType);
        using var response = await _http.SendAsync(request);
        await admission.ObserveAsync((int)response.StatusCode, await response.Content.ReadAsStringAsync(), default);
        var outcome = (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint)).Value.ShouldHaveSingleItem();
        outcome.Observations.ShouldHaveSingleItem().IntentId.ShouldBe(recorded.Intent.IntentId);
        outcome.Receipt.ShouldNotBeNull().RequestDigest.ShouldBe(recorded.Intent.RequestDigest);
        outcome.Receipt!.Kind.ShouldBe("acknowledged");
        _transport.CommittedWrites.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => session.AdmitAsync(intent, default));
        _transport.CommittedWrites.ShouldBe(1, "native attempt admission cannot replay after an acknowledgement");
    }

    [Theory]
    [InlineData("appeared")]
    [InlineData("disappeared")]
    [InlineData("changed")]
    public async Task CleanCoreAdmissionRechecksActualPolicyPresenceAndBytesAfterPreview(string change)
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        const string policyText = """{"version":1,"rules":[]}""";
        if (change != "appeared") await File.WriteAllTextAsync(paths.PolicyPath, policyText);
        var observed = (await new ConfiguredPolicyProjectionSource(paths.RepoRoot).ReadAsync(default)).Value;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var proposal = new CoreModel.Proposal(connection, "native-schema", 0,
            [new("edit", CoreModel.OpKind.Batch, key, new(key, "ado-rev:1"),
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("reviewed"))]))],
            observed.Identity, observed.Revision);
        var store = (await CleanStore.CreateAsync(Path.Combine(paths.TwigDir, "isolated-core.db"))).Value;
        var planned = CoreModel.JournalEntry.Planned(proposal.Digest, 0);
        (await store.CommitAsync(connection, 0, new(1, proposals: [proposal], journal: [planned]), default)).IsSuccess.ShouldBeTrue();
        var attempt = Guid.NewGuid();
        (await store.CommitAsync(connection, 1, new(2, proposals: [proposal], journal: [planned.Begin(attempt).Value]), default)).IsSuccess.ShouldBeTrue();
        var access = new CleanCoreAdoAccess(runtime.GetRequiredService<IAuthenticationProvider>(), _bindings,
            config, paths, origin, connection, Actor, store);
        using var session = (await access.AcquireAsync(connection, default)).Value;
        if (change == "disappeared") File.Delete(paths.PolicyPath);
        else await File.WriteAllTextAsync(paths.PolicyPath, policyText + (change == "changed" ? "\n" : ""));
        var intent = new Twig.Providers.Ado.AdoWriteIntent(proposal, 0, attempt,
            new Uri("https://dev.azure.com/fixture/" + connection.Destination + "/_apis/wit/workitems/42?api-version=7.1"),
            "PATCH", "application/json-patch+json",
            """[{"op":"test","path":"/rev","value":1},{"op":"add","path":"/fields/System.Title","value":"reviewed"}]""");
        var refused = await session.AdmitAsync(intent, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(CoreModel.ErrorCode.SchemaChanged);
        _transport.WorkRequests.ShouldBe(0);
        (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task CleanCoreAccessRefusesANewSelectedActorBeforeWorkTransport()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var store = (await CleanStore.CreateAsync(Path.Combine(paths.TwigDir, "isolated-core.db"))).Value;
        var access = new CleanCoreAdoAccess(runtime.GetRequiredService<IAuthenticationProvider>(), _bindings,
            config, paths, origin, connection, Actor, store);
        var replacement = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", false);
        SwapSelection(config, replacement.BindingId);
        (await access.AcquireAsync(connection, default)).IsSuccess.ShouldBeFalse();
        _transport.WorkRequests.ShouldBe(0);
        (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint)).Value.ShouldBeEmpty();
        (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(0);
    }

    [Fact]
    public async Task OfflineProviderLifecycle_ReadAuthorApplyReadbackUsesRealAdoComposition()
    {
        var runtime = await AttachAsync();
        var reader = runtime.GetRequiredService<IProviderWorkItemReader>();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var before = await reader.FetchAsync(42);
        before.Revision.ShouldBe(1);
        await repository.SaveAsync(before);
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var path = await ProposalAsync(runtime, "qualification", "edit", before.Id,
            before.Revision, "Offline qualified ADO title");
        var policyPath = runtime.GetRequiredService<TwigPaths>().PolicyPath;
        await File.WriteAllTextAsync(policyPath, """{"version":2,"rules":[]}""");
        (await lifecycle.PreviewAsync(path)).CanApply.ShouldBeFalse("invalid declared policy must fail closed");
        _transport.CommittedWrites.ShouldBe(0);
        File.Delete(policyPath);

        var preview = await lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue(string.Join("; ", preview.Issues.Select(i => i.Message)));
        preview.ReviewModel.ShouldNotBeNull().Operations.Single().Consequences
            .Single(e => e.Field == "System.Title").To.ShouldBe("Offline qualified ADO title");
        _transport.CommittedWrites.ShouldBe(0, "authoring and preview must not publish");
        (await lifecycle.ApplyAsync(path, new string('0', 64))).Failed.ShouldBeTrue();
        _transport.CommittedWrites.ShouldBe(0, "a different digest cannot authorize the effect");
        var result = await lifecycle.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeFalse(result.Error);
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        _transport.CommittedWrites.ShouldBe(1);
        var readback = await reader.FetchAsync(42);
        readback.Title.ShouldBe("Offline qualified ADO title");
        readback.Revision.ShouldBe(before.Revision + 1);

        var reopened = BuildRuntime(runtime.GetRequiredService<TwigConfiguration>(), runtime.GetRequiredService<TwigPaths>());
        var retained = await reopened.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42);
        retained.ShouldNotBeNull().Title.ShouldBe(readback.Title);
        retained.Revision.ShouldBe(readback.Revision);
        (await reopened.GetRequiredService<IPlanJournalRepository>().GetAsync(preview.Digest!))!
            .Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        (await reopened.GetRequiredService<IPlanLifecycleService>().ApplyAsync(path, preview.Digest!)).Failed.ShouldBeFalse();
        _transport.CommittedWrites.ShouldBe(1, "verified replay must not write again");
    }

    [Fact]
    public async Task RetiredConfirmedIntentCannotReplayBeforeOrAfterRebindingOrReimport()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "original", "retired", 42, 1, "Refined actor plan");
        var bytes = await File.ReadAllBytesAsync(file);
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        await journal.TryTransitionOperationAsync(digest, "retired", PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await journal.ConfirmAsync(digest, DateTimeOffset.UtcNow);
        var unknown = await ProposalAsync(runtime, "unrelated-unknown", "unknown", 43, 1, "Another intent");
        var unknownDigest = (await lifecycle.PreviewAsync(unknown)).Digest!;
        await journal.SaveOperationErrorAsync(unknownDigest, "unknown", "Unacknowledged earlier outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var command = new PlanCommand(lifecycle, runtime.GetRequiredService<OutputFormatterFactory>(),
            stdout: stdout, stderr: stderr);
        (await command.ReconcileAsync(file, digest, "retired", "retire",
            null, null, "json", CancellationToken.None)).ShouldBe(0, stderr.ToString());
        using (var receiptOutput = JsonDocument.Parse(stdout.ToString()))
            receiptOutput.RootElement.GetProperty("receipt").GetProperty("kind").GetString().ShouldBe("Retired");
        var workRequests = _transport.WorkRequests;
        (await lifecycle.ApplyAsync(file, digest)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(workRequests, "saved apply must refuse before any work HTTP");
        _transport.CommittedWrites.ShouldBe(0);
        _transport.RevisionOf(42).ShouldBe(1, "the original ADO revision is still admissible absent the native fence");
        (await lifecycle.PreviewAsync(file)).CanApply.ShouldBeFalse();
        var imported = await lifecycle.ValidateAsync(file);
        var captured = (await journal.GetAsync(digest))!.Origin;
        await journal.ImportAsync(imported.Plan!, imported.CanonicalJson!, digest, file, DateTimeOffset.UtcNow, origin: captured);
        (await lifecycle.ApplyAsync(file, digest)).Failed.ShouldBeTrue();
        var stored = (await journal.GetAsync(digest))!.Operations.Single();
        stored.State.ShouldBe(PlanOperationState.Confirmed);
        stored.OutcomeReceipt!.Kind.ShouldBe(PlanOutcomeKind.Retired);
        (await journal.GetUnresolvedAsync()).Select(j => j.Digest).ShouldBe(new[] { unknownDigest });

        var fresh = await ProposalAsync(runtime, "fresh", "fresh", 43, 1, "Fresh permitted plan");
        var freshDigest = (await lifecycle.PreviewAsync(fresh)).Digest!;
        (await lifecycle.ApplyAsync(fresh, freshDigest)).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        _transport.TitleOf(43).ShouldBe("Fresh permitted plan");
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var siblingBinding = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", makeDefault: false);
        SwapSelection(config, siblingBinding.BindingId);
        var reconnected = BuildRuntime(config, paths);
        var newLifecycle = reconnected.GetRequiredService<IPlanLifecycleService>();
        workRequests = _transport.WorkRequests;
        (await newLifecycle.ApplyAsync(file, digest)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(workRequests);
        (await newLifecycle.PreviewAsync(file)).CanApply.ShouldBeFalse();
        (await reconnected.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!.Origin.ShouldBe(captured);
        (await File.ReadAllBytesAsync(file)).ShouldBe(bytes);
        _transport.RevisionOf(42).ShouldBe(1);
        (await reconnected.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).Select(j => j.Digest).ShouldContain(unknownDigest);
    }

    [Fact]
    public async Task AcknowledgedCommitWithUnavailableReadbackIsSettledWithoutReissuingTheWrite()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var original = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        await repository.SaveAsync(original);
        var file = await ProposalAsync(runtime, "acknowledged", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        _transport.FailReadbackAfterCommit = true;
        var attempted = await lifecycle.ApplyAsync(file, digest);
        attempted.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.FailReadbackAfterCommit = false;
        var reconciled = await lifecycle.ReconcileAsync(file, digest, "attempt", PlanOutcomeKind.Readback);
        reconciled.Settled.ShouldBeTrue(reconciled.Error);
        var row = (await runtime.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!.Operations.Single();
        row.State.ShouldBe(PlanOperationState.Indeterminate);
        row.OutcomeReceipt!.Kind.ShouldBe(PlanOutcomeKind.Readback);
        _transport.TitleOf(42).ShouldBe("Committed actor plan");
        _transport.CommittedWrites.ShouldBe(1);
        var cached = (await repository.GetByIdAsync(42))!;
        cached.Title.ShouldBe(original.Title);
        cached.Revision.ShouldBe(original.Revision);
        (await lifecycle.ReconcileAsync(file, digest, "attempt", PlanOutcomeKind.Readback))
            .Settled.ShouldBeTrue();
        (await repository.GetByIdAsync(42))!.Title.ShouldBe(original.Title);
        _transport.CommittedWrites.ShouldBe(1);
    }

    [Theory]
    [InlineData(PlanOperationState.Failed)]
    [InlineData(PlanOperationState.Indeterminate)]
    [InlineData(PlanOperationState.Applied)]
    [InlineData(PlanOperationState.Verified)]
    [InlineData(PlanOperationState.Applied, "{")]
    [InlineData(PlanOperationState.Verified, "{")]
    public async Task AcknowledgedCommitAfterLostAppliedCasReturnsReceiptWithoutOverwritingWinner(
        PlanOperationState winningState, string winningJson = """{"rev":99}""")
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        journal.ShouldBeOfType<SqlitePlanJournalRepository>();
        runtime.GetRequiredService<ISystemWorktreeRegistry>().ShouldBeSameAs(_systemRegistry);
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42));
        (await repository.GetByIdAsync(42))!.Revision.ShouldBe(1, "mirror admission and persistence must succeed before forcing the race");
        var competing = BuildRuntime(runtime.GetRequiredService<TwigConfiguration>(), runtime.GetRequiredService<TwigPaths>());
        var competingJournal = competing.GetRequiredService<IPlanJournalRepository>();
        competingJournal.ShouldBeOfType<SqlitePlanJournalRepository>();
        competingJournal.ShouldNotBeSameAs(journal);
        var file = await ProposalAsync(runtime, "acknowledgment-fence-loss", "attempt", 42, 1, "Committed actor plan", includeTail: true);
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        PlanJournalOperation? winner = null;
        _transport.AfterCommit = async () =>
        {
            var applying = (await competingJournal.GetAsync(digest))!.Operations.Single(row => row.OpId == "attempt");
            applying.State.ShouldBe(PlanOperationState.Applying);
            applying.AppliedAt.ShouldBeNull();
            if (winningState is PlanOperationState.Applied or PlanOperationState.Verified)
            {
                (await competingJournal.TryRecordAppliedAsync(digest, "attempt", winningJson, DateTimeOffset.UtcNow))
                    .ShouldBeTrue();
                if (winningState == PlanOperationState.Verified)
                    (await competingJournal.TryTransitionOperationAsync(digest, "attempt", PlanOperationState.Applied,
                        PlanOperationState.Verified, DateTimeOffset.UtcNow)).ShouldBeTrue();
            }
            else
                await competingJournal.SaveOperationErrorAsync(digest, "attempt", "Competing unresolved classification",
                    winningState, DateTimeOffset.UtcNow, resultJson: """{"winner":"competing classification"}""");
            winner = (await competingJournal.GetAsync(digest))!.Operations.Single(row => row.OpId == "attempt");
            winner.State.ShouldBe(winningState);
            (await competingJournal.TryRecordAppliedAsync(digest, "attempt", """{"rev":999}""", DateTimeOffset.UtcNow))
                .ShouldBeFalse("the real SQLite Applying-to-Applied fence must already have been lost");
        };

        var attempted = await lifecycle.ApplyAsync(file, digest);

        winner.ShouldNotBeNull();
        _transport.CommittedWrites.ShouldBe(1);
        _transport.RevisionOf(42).ShouldBe(2);
        _transport.TitleOf(42).ShouldBe("Committed actor plan");
        _transport.ReadbacksAfterCommit.ShouldBe(0);
        (await journal.GetAsync(digest))!.Operations.Single(row => row.OpId == "attempt").ShouldBe(winner);
        attempted.Operations.Single(row => row.OpId == "tail").State.ShouldBe(PlanOperationState.Confirmed);
        _transport.TitleOf(43).ShouldBe("Another actor plan");
        var native = await InspectAcknowledgedWriteAsync(runtime);
        var reopened = await InspectAcknowledgedWriteAsync(competing);
        reopened.Receipt.ShouldBe(native.Receipt, "the native acknowledgment must remain recoverable through another registry reader");
        attempted.Failed.ShouldBeTrue();
        if (winningState is PlanOperationState.Failed or PlanOperationState.Indeterminate)
        {
            var resumed = await lifecycle.ApplyAsync(file, digest);
            resumed.Operations.Single(row => row.OpId == "attempt").ShouldBe(winner);
        }
        _transport.CommittedWrites.ShouldBe(1, "neither the losing attempt nor resume may repeat an acknowledged write");
        _transport.PatchAttempts.ShouldBe(1, "a rejected duplicate PATCH is still an unsafe duplicate send");
        (await competingJournal.GetAsync(digest))!.Operations.Single(row => row.OpId == "attempt").ShouldBe(winner);

        attempted.Error.ShouldNotBeNull().ShouldStartWith("acknowledged-effect-unsettled:");
        attempted.Error.ShouldContain("Do not retry");
        attempted.UnsettledAcknowledgments.Keys.ShouldBe(new[] { "attempt" });
        using var returned = JsonDocument.Parse(attempted.UnsettledAcknowledgments["attempt"]);
        returned.RootElement.GetProperty("rev").GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData(PlanOperationState.Applied)]
    [InlineData(PlanOperationState.Verified)]
    public async Task AcknowledgedCommitAfterLostAppliedCasResumesMatchingWinningReceipt(
        PlanOperationState winningState)
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42));
        (await repository.GetByIdAsync(42))!.Revision.ShouldBe(1);
        var competing = BuildRuntime(runtime.GetRequiredService<TwigConfiguration>(), runtime.GetRequiredService<TwigPaths>());
        var competingJournal = competing.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "acknowledgment-winning-receipt", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        _transport.AfterCommit = async () =>
        {
            (await competingJournal.GetAsync(digest))!.Operations.Single().State.ShouldBe(PlanOperationState.Applying);
            (await competingJournal.TryRecordAppliedAsync(digest, "attempt", """{"rev":2}""", DateTimeOffset.UtcNow))
                .ShouldBeTrue();
            if (winningState == PlanOperationState.Verified)
                (await competingJournal.TryTransitionOperationAsync(digest, "attempt", PlanOperationState.Applied,
                    PlanOperationState.Verified, DateTimeOffset.UtcNow)).ShouldBeTrue();
            (await competingJournal.TryRecordAppliedAsync(digest, "attempt", """{"rev":999}""", DateTimeOffset.UtcNow))
                .ShouldBeFalse();
        };

        var attempted = await lifecycle.ApplyAsync(file, digest);

        attempted.Failed.ShouldBeFalse(attempted.Error);
        attempted.UnsettledAcknowledgments.ShouldBeEmpty();
        attempted.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        using (var evidence = JsonDocument.Parse(attempted.Operations.Single().ResultJson!))
            evidence.RootElement.GetProperty("rev").GetInt32().ShouldBe(2);
        (await InspectAcknowledgedWriteAsync(runtime)).Receipt.ShouldNotBeNull();
        var winner = (await journal.GetAsync(digest))!.Operations.Single();
        var readbacks = _transport.ReadbacksAfterCommit;
        (await lifecycle.ApplyAsync(file, digest)).Failed.ShouldBeFalse();
        (await competingJournal.GetAsync(digest))!.Operations.Single().ShouldBe(winner);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.PatchAttempts.ShouldBe(1);
        _transport.ReadbacksAfterCommit.ShouldBe(readbacks);
    }

    private static async Task<ConnectionRemoteWriteInspection> InspectAcknowledgedWriteAsync(ServiceProvider runtime)
    {
        var records = await runtime.GetRequiredService<IConnectionRemoteWriteReconciliationService>().InspectAsync(
            runtime.GetRequiredService<TwigConfiguration>(), runtime.GetRequiredService<TwigPaths>());
        var native = records.ShouldHaveSingleItem();
        native.State.ShouldBe("settled");
        native.Blockers.ShouldBeEmpty();
        native.Request.Method.ShouldBe("PATCH");
        native.Request.EffectKind.ShouldBe("workitem-patch");
        var response = native.Observations.ShouldHaveSingleItem();
        response.Response.StatusCode.ShouldBe(200);
        using (var body = JsonDocument.Parse(response.Response.Body))
        {
            body.RootElement.GetProperty("id").GetInt32().ShouldBe(42);
            body.RootElement.GetProperty("rev").GetInt32().ShouldBe(2);
            body.RootElement.GetProperty("fields").GetProperty("System.Title").GetString().ShouldBe("Committed actor plan");
        }
        var receipt = native.Receipt.ShouldNotBeNull();
        receipt.Kind.ShouldBe("acknowledged");
        receipt.IntentId.ShouldBe(native.IntentId);
        receipt.RequestDigest.ShouldBe(native.Digest);
        receipt.ObservationId.ShouldBe(response.ObservationId);
        return native;
    }

    [Fact]
    public async Task RejectedReadbackReceiptWithUnattributableRevisionPreservesCleanCachedValues()
    {
        var runtime = await AttachAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var original = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        await repository.SaveAsync(original);
        original.IsDirty.ShouldBeFalse();
        original.Revision.ShouldBe(1);
        original.Title.ShouldBe("Original actor plan");

        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "unattributable", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        _transport.FailReadbackAfterCommit = true;
        (await lifecycle.ApplyAsync(file, digest)).Operations.Single().State
            .ShouldBe(PlanOperationState.Indeterminate);
        var attempted = (await journal.GetAsync(digest))!.Operations.Single();
        using (var acknowledged = JsonDocument.Parse(attempted.ResultJson!))
            acknowledged.RootElement.GetProperty("rev").GetInt32().ShouldBe(2);

        // A later edit retained the expected title but is not this attempt's acknowledged revision.
        _transport.FailReadbackAfterCommit = false;
        _transport.SetItem(42, "Committed actor plan", 3, Actor);
        var result = await lifecycle.ReconcileAsync(file, digest, "attempt", PlanOutcomeKind.Readback);

        result.Settled.ShouldBeFalse();
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        (await journal.GetUnresolvedAsync()).Select(j => j.Digest).ShouldContain(digest);
        var cached = (await repository.GetByIdAsync(42))!;
        cached.Title.ShouldBe(original.Title);
        cached.Revision.ShouldBe(original.Revision);
        cached.IsDirty.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedPublicationPreservesLocalWorkCreatedDuringReadback(bool dirtyMirror)
    {
        var runtime = await AttachAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        var original = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        await repository.SaveAsync(original);
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var file = await ProposalAsync(runtime, "concurrent-local-work", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        _transport.BeforeReadback = async () =>
        {
            if (dirtyMirror)
            {
                var editing = (await repository.GetByIdAsync(42))!;
                editing.UpdateField("System.Description", "Unpublished actor draft");
                await repository.SaveAsync(editing);
            }
            else
                await pending.AddChangeAsync(42, "note", null, null, "Unpublished actor note");
        };

        (await lifecycle.ApplyAsync(file, digest)).Operations.Single().State
            .ShouldBe(PlanOperationState.Verified);

        _transport.TitleOf(42).ShouldBe("Committed actor plan");
        var cached = (await repository.GetByIdAsync(42))!;
        cached.Title.ShouldBe(original.Title);
        cached.Revision.ShouldBe(original.Revision);
        cached.IsDirty.ShouldBe(dirtyMirror);
        if (dirtyMirror)
            cached.Fields["System.Description"].ShouldBe("Unpublished actor draft");
        else
            (await pending.GetChangesAsync(42)).ShouldHaveSingleItem().NewValue.ShouldBe("Unpublished actor note");
    }

    [Fact]
    public async Task ConcurrentDraftAfterProtectionSnapshotSurvivesVerifiedCacheRefresh()
    {
        IWorkItemRepository? competingRepository = null;
        WorkItem? draft = null;
        var attempted = false;
        var blocked = false;
        var runtime = await AttachAsync(async () =>
        {
            if (_transport.CommittedWrites == 0 || attempted) return;
            attempted = true;
            try { await competingRepository!.SaveAsync(draft!); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
            {
                // A writer may finish immediately after the readback transaction commits.
                blocked = true;
            }
        });
        var original = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(original);
        var competing = BuildRuntime(runtime.GetRequiredService<TwigConfiguration>(), runtime.GetRequiredService<TwigPaths>());
        competing.GetRequiredService<SqliteCacheStore>().GetConnection().DefaultTimeout = 1;
        competingRepository = competing.GetRequiredService<IWorkItemRepository>();
        draft = (await competingRepository.GetByIdAsync(42))!;
        draft.UpdateField("System.Description", "Concurrent unpublished draft");
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var file = await ProposalAsync(runtime, "snapshot-race", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;

        (await lifecycle.ApplyAsync(file, digest)).Operations.Single().State
            .ShouldBe(PlanOperationState.Verified);
        if (blocked) await competingRepository.SaveAsync(draft);

        var cached = (await competingRepository.GetByIdAsync(42))!;
        cached.Fields.GetValueOrDefault("System.Description").ShouldBe("Concurrent unpublished draft");
        cached.IsDirty.ShouldBeTrue();
        _transport.TitleOf(42).ShouldBe("Committed actor plan");
    }

    [Fact]
    public async Task MatchingNewerRemoteStateWithoutAcknowledgementDoesNotSettleAnUnknownAttempt()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "unknown", "unknown", 42, 1, "Coincidental newer title");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        await journal.SaveOperationErrorAsync(digest, "unknown", "Unknown transport outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        _transport.SetItem(42, "Coincidental newer title", 2, Actor);
        var result = await lifecycle.ReconcileAsync(file, digest, "unknown", PlanOutcomeKind.Readback);
        result.Settled.ShouldBeFalse();
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        (await journal.GetUnresolvedAsync()).Select(j => j.Digest).ShouldContain(digest);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyAnActuallyVerifiedReplacementOfTheOriginalEffectSettlesItsFailedAttempt()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var original = await ProposalAsync(runtime, "original", "original", 42, 1, "Replacement actor plan");
        var digest = (await lifecycle.PreviewAsync(original)).Digest!;
        _transport.RejectNextPatch = true;
        (await lifecycle.ApplyAsync(original, digest)).Operations.Single().State.ShouldBe(PlanOperationState.Failed);
        var unrelated = await ProposalAsync(runtime, "unrelated", "unrelated", 43, 1, "Unrelated successful plan");
        var unrelatedDigest = (await lifecycle.PreviewAsync(unrelated)).Digest!;
        (await lifecycle.ApplyAsync(unrelated, unrelatedDigest)).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        (await lifecycle.ReconcileAsync(original, digest, "original", PlanOutcomeKind.Superseded, unrelatedDigest, "unrelated")).Settled.ShouldBeFalse();
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        var replacement = await ProposalAsync(runtime, "replacement", "replacement", 42, 1, "Replacement actor plan");
        var replacementDigest = (await lifecycle.PreviewAsync(replacement)).Digest!;
        (await lifecycle.ApplyAsync(replacement, replacementDigest)).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var cachedReplacement = (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!;
        cachedReplacement.Title.ShouldBe("Replacement actor plan");
        cachedReplacement.Revision.ShouldBe(2);
        var writes = _transport.CommittedWrites;
        var settled = await lifecycle.ReconcileAsync(original, digest, "original", PlanOutcomeKind.Superseded, replacementDigest, "replacement");
        settled.Settled.ShouldBeTrue(settled.Error);
        var row = (await journal.GetAsync(digest))!.Operations.Single();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.OutcomeReceipt!.ReplacementDigest.ShouldBe(replacementDigest);
        (await journal.GetUnresolvedAsync()).ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(writes);
    }

    [Fact]
    public async Task ChangedAndUnknownOriginsCannotAdoptTheCurrentActorBeforeWorkHttp()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var original = await ProposalAsync(runtime, "bound", "bound", 42, 1, "Bound original intent");
        var digest = (await lifecycle.PreviewAsync(original)).Digest!;
        var legacy = await ProposalAsync(runtime, "legacy", "legacy", 43, 1, "Unknown legacy intent");
        var parsed = await lifecycle.ValidateAsync(legacy);
        await journal.ImportAsync(parsed.Plan!, parsed.CanonicalJson!, parsed.Digest!, legacy, DateTimeOffset.UtcNow);
        var requests = _transport.WorkRequests;
        (await lifecycle.ApplyAsync(legacy, parsed.Digest!)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(requests);
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var selected = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", false);
        SwapSelection(config, selected.BindingId);
        (await lifecycle.ApplyAsync(original, digest)).Failed.ShouldBeTrue();
        (await lifecycle.PreviewAsync(original)).CanApply.ShouldBeFalse();
        var reconnected = BuildRuntime(config, runtime.GetRequiredService<TwigPaths>());
        (await reconnected.GetRequiredService<IPlanLifecycleService>().ApplyAsync(original, digest)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(requests);
        (await journal.GetAsync(parsed.Digest!))!.Origin.ShouldBeNull();
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task OpenSeedIntentReadbackNeverRedrivesPublicationOrClearsTheUnknownBlocker()
    {
        var runtime = await AttachAsync();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var identity = StagedIdentity.New();
        var file = Path.Combine(paths.TwigDir, "seed-unknown.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1, workspace = new { organization = config.Organization, project = config.Project },
            operations = new[] { new { id = "seed", kind = "publish-seed", stagedIdentity = identity.ToString(), expectedFingerprint = "unproved-fingerprint" } }
        }));
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var intents = runtime.GetRequiredService<IPublishIntentRepository>();
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        var originalIntent = await intents.RecordIntentAsync(identity, "Unknown create", "Task");
        await journal.SaveOperationErrorAsync(digest, "seed", "Unknown create outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        var result = await lifecycle.ReconcileAsync(file, digest, "seed", PlanOutcomeKind.Readback);
        result.Settled.ShouldBeFalse();
        (await intents.GetOpenIntentsAsync()).Single().RecordedAt.ShouldBe(originalIntent.RecordedAt);
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        _transport.PostCount.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    private async Task<ServiceProvider> AttachAsync(Func<Task>? afterPendingRead = null)
    {
        var root = Path.Combine(_temp, "checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = "Project" };
        config.Display.Hints = false;
        var defaults = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        var paths = new TwigPaths(defaults.TwigDir, defaults.ConfigPath, defaults.DbPath, root, Path.Combine(Home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        await _bindings.RegisterPatIdentityAsync("actor", config.Organization, ActorPat);
        await _bindings.RegisterPatIdentityAsync("sibling", config.Organization, SiblingPat);
        await _bindings.CreateBindingAsync(config.Organization, config.Project, "actor", true);
        _transport.SetItem(42, "Original actor plan", 1, Actor);
        _transport.SetItem(43, "Another actor plan", 1, Actor);
        _transport.SetItem(44, "Sibling private plan", 1, Sibling);
        return BuildRuntime(config, paths, afterPendingRead);
    }

    private ServiceProvider BuildRuntime(TwigConfiguration config, TwigPaths paths, Func<Task>? afterPendingRead = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, paths.StartDir);
        services.AddSingleton<ISystemWorktreeRegistry>(_systemRegistry);
        services.AddSingleton(paths);
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices();
        if (afterPendingRead is not null)
            services.AddSingleton<IPendingChangeReader>(sp => new InterleavedPendingReader(
                (IPendingChangeReader)sp.GetRequiredService<IPendingChangeStore>(), afterPendingRead));
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        return runtime;
    }

    private sealed class InterleavedPendingReader(IPendingChangeReader inner, Func<Task> afterRead) : IPendingChangeReader
    {
        public async Task<IReadOnlyList<PendingChangeDetail>> GetAllChangesAsync(CancellationToken ct = default)
        {
            var snapshot = await inner.GetAllChangesAsync(ct);
            await afterRead();
            return snapshot;
        }
    }

    private static async Task<string> ProposalAsync(ServiceProvider runtime, string name, string opId, int itemId, int revision, string title,
        bool includeTail = false)
    {
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var file = Path.Combine(runtime.GetRequiredService<TwigPaths>().TwigDir, name + ".json");
        var operations = new[]
        {
            new { id = opId, kind = "batch", workItemId = itemId, expectedRevision = revision,
                fields = new Dictionary<string, string> { ["System.Title"] = title } }
        }.ToList();
        if (includeTail)
            operations.Add(new { id = "tail", kind = "batch", workItemId = 43, expectedRevision = 1,
                fields = new Dictionary<string, string> { ["System.Title"] = "Must not send the tail" } });
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1, workspace = new { organization = config.Organization, project = config.Project },
            operations
        }));
        return file;
    }

    private void SwapSelection(TwigConfiguration config, string binding)
    {
        // Fixture-only authoritative CAS; guarded production transition belongs to the subsequent ticket.
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _bindings.RegistryPath,
            Pooling = false,
        }.ToString());
        database.Open();
        using var current = database.CreateCommand();
        current.CommandText = "SELECT revision FROM connection_defaults WHERE connection_ref = @connection";
        current.Parameters.AddWithValue("@connection", ConnectionRefResolver.Compute(config));
        var revision = Convert.ToInt64(current.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var change = database.CreateCommand();
        change.CommandText = "UPDATE connection_defaults SET binding_id = @binding, revision = revision + 1 WHERE connection_ref = @connection AND revision = @expected";
        change.Parameters.AddWithValue("@binding", binding);
        change.Parameters.AddWithValue("@connection", ConnectionRefResolver.Compute(config));
        change.Parameters.AddWithValue("@expected", revision);
        change.ExecuteNonQuery().ShouldBe(1);
    }

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        private readonly Dictionary<int, Item> _items = new();
        private readonly Dictionary<(int Id, int Revision), Item> _history = new();
        private readonly Dictionary<int, List<(string Relation, int Target)>> _links = new();
        public int WorkRequests { get; private set; }
        public int TotalRequests { get; private set; }
        public int PostCount { get; private set; }
        public int CommittedWrites { get; private set; }
        public int PatchAttempts { get; private set; }
        public int ReadbacksAfterCommit { get; private set; }
        public List<int> QueryAfterIds { get; } = [];
        public bool RejectNextPatch { get; set; }
        public bool FailReadbackAfterCommit { get; set; }
        public Func<Task>? BeforeReadback { get; set; }
        public Func<Task>? BeforeItemReadResponse { get; set; }
        public Func<Task>? AfterCommit { get; set; }
        public bool CoreMetadataEnabled { get; set; }
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(PrincipalTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => owner.SendAsync(request, ct);
        }
        public string TitleOf(int id) => _items[id].Title;
        public int RevisionOf(int id) => _items[id].Revision;
        public void SetItem(int id, string title, int revision, string principal)
        {
            var prior = _items.GetValueOrDefault(id);
            var item = new Item(title, revision, principal, prior?.History ?? "", prior?.Resources ?? [], prior?.State ?? "To Do");
            _items[id] = item;
            _history[(id, revision)] = item;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            TotalRequests++;
            var header = request.Headers.Authorization;
            if (header?.Scheme != "Basic") return Reply(HttpStatusCode.Unauthorized, new { message = "No fixture credential" });
            var secret = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))[1..];
            var principal = secret == ActorPat ? Actor : secret == SiblingPat ? Sibling : null;
            if (principal is null) return Reply(HttpStatusCode.Unauthorized, new { message = "Unknown fixture credential" });
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new
                {
                    instanceId = "11111111-1111-1111-1111-111111111111",
                    authenticatedUser = new { id = principal, providerDisplayName = "Same display" }
                });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Same display", emailAddress = principal + "@fixture.example" });
            if (CoreMetadataEnabled && Metadata(path, request.RequestUri.Query) is { } metadata) return metadata;
            if (CoreMetadataEnabled && path.EndsWith("/wiql", StringComparison.OrdinalIgnoreCase))
            {
                using var queryBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var queryText = queryBody.RootElement.GetProperty("query").GetString().ShouldNotBeNull();
                queryText.ShouldContain("System.TeamProject");
                var afterMatch = System.Text.RegularExpressions.Regex.Match(queryText, @"\[System\.Id\]\s*>\s*(\d+)");
                var after = afterMatch.Success
                    ? int.Parse(afterMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                QueryAfterIds.Add(after);
                return Reply(HttpStatusCode.OK, new { queryType = "flat",
                    workItems = _items.Where(pair => pair.Value.Principal == principal && pair.Key > after)
                        .OrderBy(pair => pair.Key).Take(201)
                        .Select(pair => new { id = pair.Key }).ToArray() });
            }
            const string route = "/_apis/wit/workitems/";
            var routeIndex = path.IndexOf(route, StringComparison.OrdinalIgnoreCase);
            if (routeIndex < 0) return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
            WorkRequests++;
            if (request.Method == HttpMethod.Post) PostCount++;
            var segments = path[(routeIndex + route.Length)..].Split('/');
            if (CoreMetadataEnabled && request.Method == HttpMethod.Post && segments[0] == "$Task")
            {
                using var create = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var title = create.RootElement.EnumerateArray()
                    .Single(operation => operation.GetProperty("path").GetString() == "/fields/System.Title")
                    .GetProperty("value").GetString()!;
                var state = create.RootElement.EnumerateArray()
                    .Single(operation => operation.GetProperty("path").GetString() == "/fields/System.State")
                    .GetProperty("value").GetString()!;
                var createdId = _items.Keys.Max() + 1;
                SetItem(createdId, title, 1, principal);
                _items[createdId] = _items[createdId] with { State = state };
                foreach (var relation in create.RootElement.EnumerateArray()
                    .Where(operation => operation.GetProperty("path").GetString() == "/relations/-"))
                {
                    var value = relation.GetProperty("value");
                    var kind = value.GetProperty("rel").GetString();
                    if (kind == "Native.ParentReverse")
                    {
                        var parent = int.Parse(new Uri(value.GetProperty("url").GetString()!).Segments[^1], CultureInfo.InvariantCulture);
                        _items[parent].Principal.ShouldBe(principal);
                        if (!_links.TryGetValue(createdId, out var parents)) _links[createdId] = parents = [];
                        parents.Add(("Native.ParentReverse", parent));
                        if (!_links.TryGetValue(parent, out var children)) _links[parent] = children = [];
                        children.Add(("Native.ParentForward", createdId));
                    }
                    else if (kind is "ArtifactLink" or "Hyperlink")
                    {
                        var attributes = value.TryGetProperty("attributes", out var names) ? names : default;
                        var resource = new CoreModel.ResourceLink(kind, value.GetProperty("url").GetString()!,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("name", out var name) ? name.GetString() : null,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("comment", out var comment) ? comment.GetString() : null);
                        _items[createdId] = _items[createdId] with { Resources = _items[createdId].Resources.Append(resource).ToArray() };
                    }
                    else if (kind is "Native.DependencyForward" or "Native.DependencyReverse" or "Native.Association")
                    {
                        var endpoint = int.Parse(new Uri(value.GetProperty("url").GetString()!).Segments[^1], CultureInfo.InvariantCulture);
                        _items[endpoint].Principal.ShouldBe(principal);
                        if (!_links.TryGetValue(createdId, out var links)) _links[createdId] = links = [];
                        links.Add((kind, endpoint));
                        if (!_links.TryGetValue(endpoint, out var reciprocal)) _links[endpoint] = reciprocal = [];
                        reciprocal.Add((kind == "Native.DependencyForward" ? "Native.DependencyReverse"
                            : kind == "Native.DependencyReverse" ? "Native.DependencyForward" : kind, createdId));
                    }
                    else throw new InvalidOperationException("Unexpected fixture creation relationship");
                }
                _history[(createdId, 1)] = _items[createdId];
                CommittedWrites++;
                if (AfterCommit is { } afterCreate)
                {
                    AfterCommit = null;
                    await afterCreate();
                }
                return Reply(HttpStatusCode.Created, new
                {
                    id = createdId, rev = 1, fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = title, ["System.WorkItemType"] = "Task", ["System.State"] = state,
                        ["System.TeamProject"] = "Project", ["System.AreaPath"] = "fixture",
                        ["System.IterationPath"] = "fixture", ["System.History"] = "",
                        ["System.AssignedTo"] = new { id = principal, displayName = "Same display", uniqueName = principal }
                    },
                    relations = _items[createdId].Resources.Select(resource => (object)new
                    {
                        rel = resource.Relation, url = resource.Target,
                        attributes = new { name = resource.Name, comment = resource.Comment }
                    }).Concat(NativeLinks(createdId)).ToArray(),
                });
            }
            if (!int.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !_items.TryGetValue(id, out var item) || item.Principal != principal)
                return Reply(HttpStatusCode.NotFound, new { message = "Not readable by the selected principal" });
            if (request.Method == HttpMethod.Patch)
            {
                PatchAttempts++;
                if (RejectNextPatch)
                {
                    RejectNextPatch = false;
                    return Reply(HttpStatusCode.BadRequest, new { message = "Fixture rejected the transaction before committing" });
                }
                using var operations = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var title = item.Title;
                var history = item.History;
                var state = item.State;
                var resources = item.Resources.ToList();
                foreach (var operation in operations.RootElement.EnumerateArray())
                {
                    var target = operation.GetProperty("path").GetString();
                    if (target == "/rev" && operation.GetProperty("value").GetInt32() != item.Revision)
                        return Reply(HttpStatusCode.PreconditionFailed, new { message = "Revision " + item.Revision });
                    if (target == "/fields/System.Title") title = operation.GetProperty("value").GetString()!;
                    if (target == "/fields/System.History") history = operation.GetProperty("value").GetString()!;
                    if (target == "/fields/System.State") state = operation.GetProperty("value").GetString()!;
                    if (target == "/relations/-")
                    {
                        var value = operation.GetProperty("value");
                        var attributes = value.TryGetProperty("attributes", out var linkAttributes) ? linkAttributes : default;
                        resources.Add(new(value.GetProperty("rel").GetString()!, value.GetProperty("url").GetString()!,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("name", out var name) ? name.GetString() : null,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("comment", out var comment) ? comment.GetString() : null));
                    }
                }
                SetItem(id, title, item.Revision + 1, item.Principal);
                _items[id] = _items[id] with { History = history, Resources = resources.ToArray(), State = state };
                _history[(id, _items[id].Revision)] = _items[id];
                item = _items[id];
                CommittedWrites++;
                if (AfterCommit is { } afterCommit)
                {
                    AfterCommit = null;
                    await afterCommit();
                }
            }
            else if (FailReadbackAfterCommit && CommittedWrites > 0)
                throw new HttpRequestException("Fixture readback unavailable after acknowledged commit");
            else if (segments.Length == 3 && segments[1] == "revisions")
            {
                var revision = int.Parse(segments[2], CultureInfo.InvariantCulture);
                if (!_history.TryGetValue((id, revision), out item))
                    return Reply(HttpStatusCode.NotFound, new { message = "No historical revision" });
            }
            if (request.Method == HttpMethod.Get && BeforeItemReadResponse is { } beforeItemRead)
            {
                BeforeItemReadResponse = null;
                await beforeItemRead();
            }
            if (request.Method == HttpMethod.Get && CommittedWrites > 0)
            {
                ReadbacksAfterCommit++;
                if (BeforeReadback is { } beforeReadback)
                {
                    BeforeReadback = null;
                    await beforeReadback();
                }
            }
            return Reply(HttpStatusCode.OK, new
            {
                id, rev = item.Revision,
                fields = new Dictionary<string, object>
                {
                    ["System.Title"] = item.Title, ["System.WorkItemType"] = "Task", ["System.State"] = item.State,
                    ["System.TeamProject"] = "Project",
                    ["System.History"] = item.History,
                    ["System.AreaPath"] = "fixture", ["System.IterationPath"] = "fixture",
                    ["System.AssignedTo"] = new { id = item.Principal, displayName = "Same display", uniqueName = item.Principal }
                },
                relations = item.Resources.Select(resource => new
                {
                    rel = resource.Relation, url = resource.Target,
                    attributes = new { name = resource.Name, comment = resource.Comment }
                }).Cast<object>().Concat(NativeLinks(id)).ToArray()
            });
        }

        private object[] NativeLinks(int id) => _links.GetValueOrDefault(id, [])
            .Select(link => (object)new
            {
                rel = link.Relation,
                url = "https://dev.azure.com/fixture/_apis/wit/workItems/" + link.Target.ToString(CultureInfo.InvariantCulture),
                attributes = new { },
            }).ToArray();

        private static HttpResponseMessage? Metadata(string path, string query)
        {
            if (path.Contains("/_apis/git/repositories/", StringComparison.Ordinal))
            {
                if (path.EndsWith("/refs", StringComparison.Ordinal))
                    return Reply(HttpStatusCode.OK, new { value = new[] { new { name = "refs/heads/feature/rooted", objectId = new string('a', 40) } } });
                return Reply(HttpStatusCode.OK, new { id = "44444444-4444-4444-4444-444444444444", name = "FixtureRepo",
                    project = new { id = "22222222-2222-2222-2222-222222222222" } });
            }
            if (path.Contains("/_apis/projects/", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new
                {
                    id = "22222222-2222-2222-2222-222222222222", name = "Project",
                    capabilities = new { processTemplate = new { templateTypeId = "33333333-3333-3333-3333-333333333333" } }
                });
            if (path.EndsWith("/fields", StringComparison.Ordinal))
            {
                var names = new[] { "System.Title", "System.State", "System.TeamProject", "System.WorkItemType",
                    "System.AreaPath", "System.IterationPath", "System.AssignedTo", "System.History" };
                return Reply(HttpStatusCode.OK, new { value = names.Select(name => new
                {
                    referenceName = name, type = "string", readOnly = name is "System.TeamProject" or "System.WorkItemType",
                    isIdentity = name == "System.AssignedTo", isPicklist = false, required = false
                }).ToArray() });
            }
            if (path.EndsWith("/workitemrelationtypes", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = new object[]
                {
                    new { referenceName = "Hyperlink", attributes = new { usage = "resourceLink", enabled = true } },
                    new { referenceName = "ArtifactLink", attributes = new { usage = "resourceLink", enabled = true } },
                    new { referenceName = "Native.ParentForward", attributes = new { usage = "workItemLink", enabled = true, directional = true, isForward = true, topology = "tree", oppositeEndReferenceName = "Native.ParentReverse" } },
                    new { referenceName = "Native.ParentReverse", attributes = new { usage = "workItemLink", enabled = true, directional = true, isForward = false, topology = "tree", oppositeEndReferenceName = "Native.ParentForward" } },
                    new { referenceName = "Native.DependencyForward", attributes = new { usage = "workItemLink", enabled = true, directional = true, isForward = true, topology = "dependency", oppositeEndReferenceName = "Native.DependencyReverse" } },
                    new { referenceName = "Native.DependencyReverse", attributes = new { usage = "workItemLink", enabled = true, directional = true, isForward = false, topology = "dependency", oppositeEndReferenceName = "Native.DependencyForward" } },
                    new { referenceName = "Native.Association", attributes = new { usage = "workItemLink", enabled = true, directional = false, isForward = true, topology = "network", oppositeEndReferenceName = "Native.Association" } },
                } });
            if (path.EndsWith("/workitemtypecategories", StringComparison.Ordinal)
                || path.EndsWith("/rules", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = Array.Empty<object>() });
            if (path.EndsWith("/workitemtypes", StringComparison.OrdinalIgnoreCase) && query.Contains("$expand=all"))
                return Reply(HttpStatusCode.OK, new { value = new[] { new
                {
                    referenceName = "Fixture.Task", transitions = new Dictionary<string, object[]>
                    {
                        [""] = [new { to = "To Do" }], ["To Do"] = [new { to = "Doing" }],
                        ["Doing"] = [new { to = "Done" }], ["Done"] = [new { to = "Doing" }]
                    }
                } } });
            if (path.EndsWith("/workItemTypes", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = new[] { new { referenceName = "Fixture.Task", name = "Task", isDisabled = false } } });
            if (path.EndsWith("/states", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = new[]
                {
                    new { name = "To Do", stateCategory = "Proposed" }, new { name = "Doing", stateCategory = "InProgress" },
                    new { name = "Done", stateCategory = "Completed" }
                } });
            return null;
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        private sealed record Item(string Title, int Revision, string Principal, string History, CoreModel.ResourceLink[] Resources, string State);
    }
}
