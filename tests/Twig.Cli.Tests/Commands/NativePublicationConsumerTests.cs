using Twig.Infrastructure.Telemetry;
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
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Policy;
using Xunit;
using CoreModel = Twig.Core;
using CleanStore = Twig.Store.Sqlite.SqliteStore;
using Twig.Infrastructure.Services;

namespace Twig.Cli.Tests.Commands;

/// <summary>Native receipts over real attached stores and independently maintained, principal-private remote state.</summary>
public sealed partial class NativePublicationConsumerTests : IAsyncLifetime
{
    private static void AssertNativeDependencyClosure()
    {
        foreach (var assembly in new[] { typeof(TwigCommands).Assembly, typeof(ConnectionBindingService).Assembly })
            assembly.GetReferencedAssemblies().ShouldNotContain(reference => reference.Name == "Twig.Domain");
    }

    private static void AssertNativeRegistrations(IServiceCollection services)
    {
        AssertNativeDependencyClosure();
        services.ShouldNotContain(service => service.ServiceType.Assembly.GetName().Name == "Twig.Domain"
            || service.ServiceType.Name == "IPendingChangeFlusher");
    }

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
    public async Task CoreLocalRelatedCannotFallbackFromNativeAdoAssociation()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var before = (await store.ReadAsync(connection, default)).Value;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            foreach (var verb in new[] { "related", "unrelate" })
            {
                stderr.GetStringBuilder().Clear();
                RunCoreLink(paths, ["link", "local", verb, "43", "--id", "42", "-o", "json"]).ShouldBe(1);
                stdout.ToString().ShouldBeEmpty();
                using var error = JsonDocument.Parse(stderr.ToString());
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("local-only symmetric Association");
            }
            _transport.PatchAttempts.ShouldBe(0);
            var after = (await store.ReadAsync(connection, default)).Value;
            after.Version.ShouldBe(before.Version);
            after.Relationships.ShouldBeEmpty();
            after.Proposals.ShouldBeEmpty();
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    [Fact]
    public async Task CoreLinkMachineChildOptionRequiresIdBeforeAnyHttp()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        (await store.ReadAsync(connection, default)).Value.Proposals.ShouldBeEmpty();
        _transport.PatchAttempts.ShouldBe(0);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var requests = _transport.TotalRequests;
            foreach (var format in new[] { "json", "json-full", "json-compact", "minimal" })
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                RunCoreLink(paths, ["link", "parent", "43", "--child", "42", "-o", format])
                    .ShouldBe(2, stderr.ToString());
                stdout.ToString().ShouldBeEmpty();
                stderr.ToString().ShouldContain("--id");
                if (format.StartsWith("json", StringComparison.Ordinal))
                {
                    using var error = JsonDocument.Parse(stderr.ToString());
                    error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
                }
                _transport.TotalRequests.ShouldBe(requests, "--child is not machine authorization, even when valid");
            }
            _transport.PatchAttempts.ShouldBe(0);
            (await store.ReadAsync(connection, default)).Value.Proposals.ShouldBeEmpty();
            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            RunCoreLink(paths, ["link", "parent", "43", "--id", "42", "--child", "42", "-o", "json"])
                .ShouldBe(0, stderr.ToString());
            (await store.ReadAsync(connection, default)).Value.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Verified);
            _transport.PatchAttempts.ShouldBe(1);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreLinkHumanActivePublishedUuidUsesPermanentMappingWithoutRewritingHistory()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return RunCoreLink(paths, args);
            }
            Run("seed", "new", "--title", "Active UUID publication", "--type", "Task", "--no-parent", "-o", "json")
                .ShouldBe(0, stderr.ToString());
            using var seedOutput = JsonDocument.Parse(stdout.ToString());
            var alias = seedOutput.RootElement.GetProperty("id").GetInt32();
            var seed = (await store.ReadAsync(connection, default)).Value.Drafts.Single();
            seed.Key.IsLocal.ShouldBeTrue();
            Run("set", alias.ToString(CultureInfo.InvariantCulture)).ShouldBe(0, stderr.ToString());
            var contextFile = Path.Combine(paths.TwigDir, "core-context.json");
            var originalContext = await File.ReadAllBytesAsync(contextFile);
            using (var context = JsonDocument.Parse(originalContext))
                context.RootElement.GetProperty("history")[0].GetProperty("localId").GetGuid().ShouldBe(seed.Key.LocalId);
            var beforeUnmapped = (await store.ReadAsync(connection, default)).Value;
            beforeUnmapped.Mappings.ShouldBeEmpty();
            Run("link", "related", "43").ShouldBe(1);
            stderr.ToString().ShouldContain("seed");
            _transport.PatchAttempts.ShouldBe(0);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(beforeUnmapped.Version);
            Run("seed", "publish", alias.ToString(CultureInfo.InvariantCulture)).ShouldBe(0, stderr.ToString());
            var published = (await store.ReadAsync(connection, default)).Value;
            var mapping = published.Mappings.Single();
            mapping.Local.ShouldBe(seed.Key);
            published.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Verified);
            published.Drafts.ShouldBeEmpty();
            var creation = CoreModel.Core.ProposalBytes(published.Proposals.Single());
            (await File.ReadAllBytesAsync(contextFile)).ShouldBe(originalContext);
            Run("link", "related", "43").ShouldBe(0, stderr.ToString());
            var linked = (await store.ReadAsync(connection, default)).Value;
            linked.Mappings.Single().ShouldBe(mapping);
            linked.Drafts.ShouldBeEmpty();
            CoreModel.Core.ProposalBytes(linked.Proposals.Single(value => value.Digest == published.Proposals.Single().Digest))
                .ShouldBe(creation);
            var relationship = linked.Proposals.Single(value => value.Digest != published.Proposals.Single().Digest);
            relationship.Operations.Single().Item.ShouldBe(mapping.Remote);
            linked.Journal.Single(row => row.Digest == relationship.Digest).Status.ShouldBe(CoreModel.OpStatus.Verified);
            _transport.RelationshipPatches.Single().Id.ToString(CultureInfo.InvariantCulture).ShouldBe(mapping.Remote.NativeId);
            (await File.ReadAllBytesAsync(contextFile)).ShouldBe(originalContext);

            _transport.LoseNextPatchAcknowledgement = true;
            Run("link", "successor", "43").ShouldBe(1);
            var fenced = (await store.ReadAsync(connection, default)).Value;
            fenced.Journal.ShouldContain(row => row.Status == CoreModel.OpStatus.Indeterminate);
            var fencedWrites = _transport.CommittedWrites;
            Run("link", "successor", "42").ShouldBe(1);
            _transport.CommittedWrites.ShouldBe(fencedWrites, "mapped human context cannot bypass the shared native endpoint fence");
            _transport.PatchAttempts.ShouldBe(2);
            (await store.ReadAsync(connection, default)).Value.Journal.Select(row => (row.Digest, row.Operation, row.Attempt, row.Status, row.Receipts.Length))
                .ShouldBe(fenced.Journal.Select(row => (row.Digest, row.Operation, row.Attempt, row.Status, row.Receipts.Length)));
            (await File.ReadAllBytesAsync(contextFile)).ShouldBe(originalContext);

            Run("seed", "new", "--title", "Unknown active UUID", "--type", "Task", "--no-parent", "-o", "json")
                .ShouldBe(0, stderr.ToString());
            using var unknownOutput = JsonDocument.Parse(stdout.ToString());
            var unknownAlias = unknownOutput.RootElement.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            Run("set", unknownAlias).ShouldBe(0, stderr.ToString());
            var unknownContext = await File.ReadAllBytesAsync(contextFile);
            _transport.AfterCommit = () => throw new IOException("Fixture lost active seed acknowledgement");
            Run("seed", "publish", unknownAlias).ShouldBe(1);
            var unknown = (await store.ReadAsync(connection, default)).Value;
            unknown.Journal.ShouldContain(row => row.Status == CoreModel.OpStatus.Indeterminate);
            unknown.Mappings.ShouldHaveSingleItem();
            var writes = _transport.CommittedWrites;
            Run("link", "successor", "43").ShouldBe(1);
            stderr.ToString().ShouldContain("seed");
            _transport.CommittedWrites.ShouldBe(writes);
            _transport.PatchAttempts.ShouldBe(2);
            var retained = (await store.ReadAsync(connection, default)).Value;
            retained.Version.ShouldBe(unknown.Version, "an unmapped active UUID must not change drafts, mappings or journal");
            (await File.ReadAllBytesAsync(contextFile)).ShouldBe(unknownContext);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreLinkRouteUsesNativeJournalWithoutLegacyGraph()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        _transport.SetItem(43, "Other endpoint", 7, Actor);
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "relationships-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["link", "related", "43", "--id", "42", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        root.GetService<CoreCommandAdapter>().ShouldNotBeNull("exercise the pinned Core composition");
        AssertNativeDependencyClosure();
        typeof(TwigCommands).Assembly.GetType("Twig.Commands.IPendingChangeFlusher").ShouldBeNull();
        AssertNativeDependencyClosure();
        using (var handle = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value)
        {
            var localFrom = CoreModel.ItemKey.Local(Guid.CreateVersion7());
            var localTo = CoreModel.ItemKey.Local(Guid.CreateVersion7());
            var current = (await store.ReadAsync(connection, default)).Value;
            (await store.CommitAsync(connection, current.Version, new(current.Version + 1, [], [
                new(localFrom, localTo, "retained-local-kind", CoreModel.RelationshipOrigin.Local, 1, true)
            ], [], [], [], [], []), default)).IsSuccess.ShouldBeTrue();
        }
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return RunCoreLink(paths, args);
            }
            Run("link", "related", "43", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            using var result = JsonDocument.Parse(stdout.ToString());
            result.RootElement.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
            result.RootElement.GetProperty("status").GetString().ShouldBe("Verified");
            result.RootElement.GetProperty("links")[0].GetProperty("linkType").GetString().ShouldBe("related");
            var snapshot = (await store.ReadAsync(connection, default)).Value;
            snapshot.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Verified);
            _transport.PatchAttempts.ShouldBe(1);
            AssertPatch(42, 1, "Native.Association", 43);
            Run("link", "unrelate", "43", "--id", "42", "-o", "json-full").ShouldBe(0, stderr.ToString());
            AssertPatch(42, 2, null, null);
            Run("link", "predecessor", "43", "--id", "42", "-o", "json-compact").ShouldBe(0, stderr.ToString());
            AssertPatch(43, 9, "Native.DependencyForward", 42);
            Run("link", "unlink", "predecessor", "43", "--id", "42", "-o", "minimal").ShouldBe(0, stderr.ToString());
            AssertPatch(43, 10, null, null);
            Run("link", "successor", "43", "--id", "42", "-o", "human").ShouldBe(0, stderr.ToString());
            AssertPatch(42, 5, "Native.DependencyForward", 43);
            Run("link", "unlink", "successor", "43", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            AssertPatch(42, 6, null, null);
            Run("link", "parent", "43", "--child", "42", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            AssertPatch(43, 13, "Native.ParentForward", 42);
            Run("link", "unparent", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            AssertPatch(43, 14, null, null);
            snapshot = (await store.ReadAsync(connection, default)).Value;
            snapshot.Journal.Length.ShouldBe(8);
            snapshot.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified
                && row.Receipts.Length == 2 && row.Receipts[0].Status == CoreModel.OpStatus.Applied);
            snapshot.Proposals.ShouldAllBe(proposal => proposal.Operations.Length == 1
                && proposal.Operations[0].ExpectedRevision!.Item == proposal.Operations[0].Item);
            snapshot.Relationships.Single(edge => edge.Origin == CoreModel.RelationshipOrigin.Local).Deleted.ShouldBeTrue();
            snapshot.Relationships.Single(edge => edge.Origin == CoreModel.RelationshipOrigin.Local).LocalVersion.ShouldBe(1);
            _transport.PostCount.ShouldBe(0);
            _transport.ReadbacksAfterCommit.ShouldBeGreaterThan(8);

            void AssertPatch(int source, int revision, string? relation, int? target)
            {
                var patch = _transport.RelationshipPatches.Last();
                patch.Id.ShouldBe(source);
                using var body = JsonDocument.Parse(patch.Body);
                var entries = body.RootElement.EnumerateArray().ToArray();
                entries.Length.ShouldBe(2);
                entries[0].GetRawText().ShouldBe($$"""{"op":"test","path":"/rev","value":{{revision}}}""");
                if (relation is null)
                    entries[1].GetRawText().ShouldBe("""{"op":"remove","path":"/relations/0"}""");
                else
                {
                    entries[1].GetProperty("op").GetString().ShouldBe("add");
                    entries[1].GetProperty("path").GetString().ShouldBe("/relations/-");
                    var value = entries[1].GetProperty("value");
                    value.EnumerateObject().Count().ShouldBe(2);
                    value.GetProperty("rel").GetString().ShouldBe(relation);
                    value.GetProperty("url").GetString().ShouldBe("https://dev.azure.com/fixture/_apis/wit/workItems/" + target);
                }
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreLinkRefusalsAndAmbientSelectionNeverUseLegacyOrDispatchUnsupportedIntent()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return RunCoreLink(paths, args);
            }
            var requests = _transport.TotalRequests;
            foreach (var invocation in new[]
            {
                new[] { "related", "43" }, ["related", "43", "--id", "0"], ["related", "-1", "--id", "42"],
                ["related", "42", "--id", "42"], ["parent", "43", "42", "--id", "43"],
                ["unlink", "bogus", "43", "--id", "42"], ["unlink", "parent", "43", "--id", "42"],
                ["parent", "43", "--child", "42", "--id", "43"], ["parent", "43", "42"],
            })
            {
                Run(["link", .. invocation, "-o", "json"]).ShouldBe(2, stderr.ToString());
                stdout.ToString().ShouldBeEmpty();
                using var error = JsonDocument.Parse(stderr.ToString());
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            }
            Run("link", "related", "43", "--id", "42", "-o", "ids").ShouldBe(2);
            Run("link", "reparent", "43", "--id", "42", "-o", "json").ShouldBe(1);
            Run("link", "related", "43", "--id", "42", "--comment", "", "-o", "json").ShouldBe(1);
            _transport.TotalRequests.ShouldBe(requests, "argument and unsupported-shape refusals precede native HTTP");
            Run("link", "related", "999", "--id", "42", "-o", "json").ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(0);
            Run("set", "42").ShouldBe(0, stderr.ToString());
            requests = _transport.TotalRequests;
            Run("link", "related", "43", "-o", "minimal").ShouldBe(2);
            _transport.TotalRequests.ShouldBe(requests, "even a valid active context is not machine authority");
            Run("link", "related", "43").ShouldBe(0, stderr.ToString());
            Run("link", "related", "43", "--id", "42", "-o", "json").ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(1, "duplicate link is not replayed");
            _transport.BeforeItemReadResponse = () =>
            {
                _transport.SetItem(42, "Concurrent removal revision", _transport.RevisionOf(42) + 1, Actor);
                return Task.CompletedTask;
            };
            Run("link", "unrelate", "43", "--id", "42", "-o", "json").ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(1, "a removal with a moved revision refuses without dispatch");
            Run("link", "unrelate", "43", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            _transport.BeforeItemReadResponse = () =>
            {
                _transport.SetItem(42, "Concurrent native revision", _transport.RevisionOf(42) + 1, Actor);
                return Task.CompletedTask;
            };
            Run("link", "related", "43", "--id", "42", "-o", "json").ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(2, "a moved observed revision refuses before dispatch");
            (await store.ReadAsync(connection, default)).Value.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            Run("link", "parent", "43", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
            _transport.SetItem(44, "Another parent", 1, Actor);
            Run("link", "parent", "44", "--id", "42", "-o", "json").ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(3, "parent must not silently become reparent");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreLinkTelemetryUsesStaticMetadataAndCannotChangeTheOutcome()
    {
        var client = Substitute.For<ITelemetryClient>();
        IReadOnlyDictionary<string, string>? captured = null;
        client.When(value => value.TrackEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, double>>())).Do(call =>
        {
            captured = call.ArgAt<IReadOnlyDictionary<string, string>>(1);
            throw new InvalidOperationException("Optional telemetry failed");
        });
        using var stderr = new StringWriter();
        var command = new LinkCommand(new Twig.Rendering.RendererFactory(), telemetryClient: client, stderr: stderr);
        (await command.UnlinkDependencyAsync("private-user-content", 43, 42, "json")).ShouldBe(2);
        captured.ShouldNotBeNull();
        captured["command"].ShouldBe("link-unlink");
        captured["exit_code"].ShouldBe("2");
        captured.Values.ShouldNotContain("private-user-content");
        captured.Keys.Order().ShouldBe(new[] { "command", "exit_code", "os_platform", "output_format", "twig_version" });
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(false, CoreModel.OpStatus.Partial)]
    [InlineData(true, CoreModel.OpStatus.Indeterminate)]
    public async Task CoreLinkUnresolvedReadbackFencesBothEndpointsWithoutReplay(bool unknown, CoreModel.OpStatus expected)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            _transport.LoseNextPatchAcknowledgement = unknown;
            if (!unknown) _transport.AfterCommit = () =>
            {
                _transport.ForgetAssociation(42, 43);
                return Task.CompletedTask;
            };
            var args = new[] { "link", "related", "43", "--id", "42", "-o", "json" };
            RunCoreLink(paths, args).ShouldBe(1, stderr.ToString());
            stdout.ToString().ShouldBeEmpty();
            using (var error = JsonDocument.Parse(stderr.ToString()))
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            var retained = (await store.ReadAsync(connection, default)).Value;
            retained.Journal.Single().Status.ShouldBe(expected);
            retained.Journal.Single().Attempt.ShouldNotBe(Guid.Empty);
            _transport.PatchAttempts.ShouldBe(1);
            _transport.SetItem(44, "Next endpoint", 1, Actor);
            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            RunCoreLink(paths, ["link", "successor", "44", "--id", "43", "-o", "json"]).ShouldBe(1);
            _transport.PatchAttempts.ShouldBe(1, "the other endpoint is fenced under a different digest too");
            var current = (await store.ReadAsync(connection, default)).Value;
            current.Journal.Single().Status.ShouldBe(expected);
            current.Proposals.Single().Digest.ShouldBe(retained.Proposals.Single().Digest);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private async Task<(TwigPaths Paths, CoreModel.Connection Connection, CleanStore Store)> RelationshipWorkspaceAsync()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        _transport.SetItem(43, "Other endpoint", 7, Actor);
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "relationships-core.db");
        var created = await CleanStore.CreateAsync(database);
        created.IsSuccess.ShouldBeTrue();
        (await CoreWorkspaceFactory.RecordNewAsync(paths, created.Value, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        return (paths, connection, created.Value);
    }

    private int RunCoreLink(TwigPaths paths, string[] args) => Twig.TwigConsoleApp.Run(args, registration =>
    {
        registration.AddSingleton<IConnectionBindingService>(_bindings);
        registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        registration.AddTwigWorkspaceServices(paths.StartDir, args);
    });

    [Fact]
    public async Task CoreProcessInspectionUsesDiscoveredRulesWithoutLegacyCommandsOrWorkReads()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        _transport.CloseGateEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "process-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["process", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        root.GetService<CoreCommandAdapter>().ShouldNotBeNull();
        AssertNativeDependencyClosure();
        typeof(TwigCommands).Assembly.GetType("Twig.Commands.IPendingChangeFlusher").ShouldBeNull();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return Twig.TwigConsoleApp.Run(args, registration =>
                {
                    registration.AddSingleton<IConnectionBindingService>(_bindings);
                    registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                    registration.AddTwigWorkspaceServices(paths.StartDir, args);
                });
            }
            var before = (await store.ReadAsync(connection, default)).Value;
            var workReads = _transport.WorkRequests;
            Run("process", "-o", "json").ShouldBe(0, stderr.ToString());
            using (var list = JsonDocument.Parse(stdout.ToString()))
            {
                list.RootElement.GetProperty("totalTypes").GetInt32().ShouldBe(1);
                list.RootElement.GetProperty("types")[0].GetProperty("typeName").GetString().ShouldBe("Fixture.Task");
            }
            Run("process", "Fixture.Task", "--refresh", "-o", "json").ShouldBe(0, stderr.ToString());
            using (var detail = JsonDocument.Parse(stdout.ToString()))
            {
                detail.RootElement.GetProperty("states").GetArrayLength().ShouldBe(3);
                detail.RootElement.GetProperty("fields").EnumerateArray().ShouldContain(field =>
                    field.GetProperty("referenceName").GetString() == "Fixture.CloseEvidence");
                var transitions = detail.RootElement.GetProperty("transitions");
                transitions.GetArrayLength().ShouldBe(3, "use native edges, never an invented all-pairs graph");
                transitions.EnumerateArray().ShouldNotContain(edge =>
                    edge.GetProperty("from").GetString() == "To Do" && edge.GetProperty("to").GetString() == "Done");
            }
            Run("process", "description", "Fixture.Task", "-o", "json").ShouldBe(0, stderr.ToString());
            var full = stdout.ToString();
            using (var description = JsonDocument.Parse(full))
            {
                description.RootElement.GetProperty("descriptorVersion").GetString().ShouldBe("core-schema-1");
                description.RootElement.GetProperty("scope").GetString().ShouldBe("provider-discovered-core-schema");
                description.RootElement.GetProperty("knownGaps").EnumerateArray().ShouldContain(gap =>
                    gap.GetString()!.Contains("layout", StringComparison.OrdinalIgnoreCase));
                var rules = description.RootElement.GetProperty("types")[0].GetProperty("rules");
                var gate = rules.EnumerateArray().Single(rule =>
                    rule.GetProperty("field").GetString() == "Fixture.CloseEvidence");
                gate.GetProperty("action").GetString().ShouldBe("Required");
                gate.GetProperty("conditions")[0].GetProperty("field").GetString().ShouldBe("System.State");
                gate.GetProperty("conditions")[0].GetProperty("value").GetProperty("value").GetString().ShouldBe("Done");
                gate.GetProperty("disabled").GetBoolean().ShouldBeFalse();
            }
            var file = Path.Combine(paths.TwigDir, "process-description.json");
            Run("process", "description", "Fixture.Task", "--out", file, "-o", "json").ShouldBe(0, stderr.ToString());
            (await File.ReadAllTextAsync(file)).Trim().ShouldBe(full.Trim());
            Run("process", "description", "Fixture.Task", "--sections", "requirements",
                "--fields", "Fixture.CloseEvidence", "-o", "json").ShouldBe(0, stderr.ToString());
            using (var compact = JsonDocument.Parse(stdout.ToString()))
            {
                compact.RootElement.GetProperty("types")[0].GetProperty("rules").GetArrayLength().ShouldBe(1);
                compact.RootElement.GetProperty("types")[0].TryGetProperty("fields", out _).ShouldBeFalse();
            }
            foreach (var format in new[] { "human", "minimal", "json-compact" })
            {
                Run("process", "Fixture.Task", "-o", format).ShouldBe(0, stderr.ToString());
                stdout.ToString().ShouldNotBeNullOrWhiteSpace();
                Run("process", "description", "Fixture.Task", "-o", format).ShouldBe(0, stderr.ToString());
                stdout.ToString().ShouldNotBeNullOrWhiteSpace();
            }
            Run("process", "Absent.Type", "-o", "json").ShouldBe(1);
            stderr.ToString().ShouldContain("Unknown");
            Run("process", "Task", "-o", "json").ShouldBe(0, stderr.ToString());
            var requests = _transport.TotalRequests;
            Run("states", "-o", "json").ShouldBe(2);
            Run("process", "-o", "ids").ShouldBe(2);
            Run("process", "layout", "Fixture.Task", "-o", "json").ShouldBe(1);
            stderr.ToString().ShouldContain("layout");
            Run("process", "--org", "fixture", "--project", "Project", "-o", "json").ShouldBe(1);
            stderr.ToString().ShouldContain("override");
            foreach (var target in new[]
            {
                new[] { "--org", "fixture" },
                ["--project", "Project"],
                ["--org", "", "--project", ""],
                ["--org", "fixture", "--project", " "],
            })
            {
                Run(["process", .. target, "-o", "json"]).ShouldBe(2, stderr.ToString());
                stdout.ToString().ShouldBeEmpty();
                using var invalidTarget = JsonDocument.Parse(stderr.ToString());
                invalidTarget.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("--project");
            }
            Run("process", "--org", "another-target", "--project", "Project", "-o", "json").ShouldBe(1);
            using (var conflict = JsonDocument.Parse(stderr.ToString()))
                conflict.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("manifest is authoritative");
            Run("process", "description", "Fixture.Task", "--sections", "bogus", "-o", "json").ShouldBe(2);
            Run("process", "description", "Fixture.Task", "--fields", "Fixture.CloseEvidence", "-o", "json").ShouldBe(2);
            _transport.TotalRequests.ShouldBe(requests, "capability/argument refusals must happen before any ambient or override HTTP");
            _transport.FailSchema = true;
            Run("process", "-o", "json").ShouldBe(1);
            stdout.ToString().ShouldBeEmpty();
            using (var error = JsonDocument.Parse(stderr.ToString()))
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            Run("process", "description", "Fixture.Task", "--out", file, "-o", "json").ShouldBe(1);
            (await File.ReadAllTextAsync(file)).Trim().ShouldBe(full.Trim(), "failed reads must not replace a retained description");
            _transport.WorkRequests.ShouldBe(workReads);
            _transport.PatchAttempts.ShouldBe(0);
            _transport.PostCount.ShouldBe(0);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(before.Version);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreProposalInspectionPreservesTypedDraftsAndExactJournalWithoutLegacyReaders()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "inspection-core.db");
        var created = await CleanStore.CreateAsync(database);
        created.IsSuccess.ShouldBeTrue();
        var store = created.Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["pending", "-o", "json"], Path.Combine(Home, "display.json"));
        using var root = services.BuildServiceProvider();
        typeof(TwigCommands).Assembly.GetType("Twig.Commands.PlanCommand").ShouldBeNull("the fixture must exercise the missing Core route, not legacy inspection");
        AssertNativeDependencyClosure();
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var workspace = admitted.Workspace;
        var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        (await workspace.StageEditAsync(key,
            new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Captured edit"))]), default)).IsSuccess.ShouldBeTrue();
        var seed = (await workspace.StageSeedAsync("Fixture.Task",
            new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Captured local seed")),
                KeyValuePair.Create("System.Description", CoreModel.FieldValue.Null)]), default,
            parent: key, parentRelation: "System.LinkTypes.Hierarchy-Forward",
            resource: new("Hyperlink", "https://example.test/captured-evidence", "Captured evidence"),
            links: [new(key, "System.LinkTypes.Dependency-Forward", true)])).Value;
        var proposal = (await workspace.PreviewDraftAsync(key, default)).Value.Proposal.ShouldNotBeNull();
        var file = Path.Combine(paths.TwigDir, "inspection-proposal.json");
        await File.WriteAllBytesAsync(file, CoreModel.Core.ProposalBytes(proposal));
        var capture = (await store.ReadAsync(connection, default)).Value;
        capture.Drafts.Length.ShouldBe(2);
        capture.Journal.ShouldBeEmpty();
        var telemetry = Substitute.For<ITelemetryClient>();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return Twig.TwigConsoleApp.Run(args, registration =>
                {
                    registration.AddSingleton<IConnectionBindingService>(_bindings);
                    registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                    registration.AddSingleton(telemetry);
                    registration.AddTwigWorkspaceServices(paths.StartDir, args, Path.Combine(Home, "display.json"));
                });
            }
            var requests = _transport.TotalRequests;
            Run("proposal", "validate", "--file", file, "-o", "json").ShouldBe(0, stderr.ToString());
            using (var validation = JsonDocument.Parse(stdout.ToString()))
            {
                validation.RootElement.GetProperty("digest").GetString().ShouldBe(proposal.Digest);
                validation.RootElement.GetProperty("validationScope").GetString().ShouldBe("canonical-format");
                validation.RootElement.GetProperty("applicationEvidenceChecked").GetBoolean().ShouldBeFalse();
            }
            Run("proposal", "status", "--file", file, "-o", "json").ShouldBe(1, stderr.ToString());
            using (var status = JsonDocument.Parse(stdout.ToString()))
                status.RootElement.GetProperty("found").GetBoolean().ShouldBeFalse();
            Run("pending", "-o", "json").ShouldBe(0, stderr.ToString());
            using (var pending = JsonDocument.Parse(stdout.ToString()))
            {
                pending.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
                var drafts = pending.RootElement.GetProperty("drafts").EnumerateArray().ToArray();
                var remote = drafts.Single(d => !d.GetProperty("isSeed").GetBoolean());
                var title = remote.GetProperty("fields").GetProperty("System.Title");
                title.GetProperty("oldPresent").GetBoolean().ShouldBeTrue();
                title.GetProperty("oldValue").GetString().ShouldBe(capture.Drafts.Single(d => d.Key == key).Baseline!.Fields["System.Title"].Text);
                title.GetProperty("newValue").GetString().ShouldBe("Captured edit");
                var local = drafts.Single(d => d.GetProperty("isSeed").GetBoolean());
                local.GetProperty("item").GetProperty("localId")
                    .GetString().ShouldBe(seed.Key.LocalId.ToString("D"));
                local.GetProperty("parent").GetProperty("nativeId").GetString().ShouldBe("42");
                local.GetProperty("links")[0].GetProperty("other").GetProperty("nativeId").GetString().ShouldBe("42");
                local.GetProperty("links")[0].GetProperty("outgoing").GetBoolean().ShouldBeTrue();
                local.GetProperty("resource").GetProperty("target").GetString().ShouldBe("https://example.test/captured-evidence");
                var description = local.GetProperty("fields").GetProperty("System.Description");
                description.GetProperty("oldPresent").GetBoolean().ShouldBeFalse();
                description.GetProperty("newValue").ValueKind.ShouldBe(JsonValueKind.Null);
            }
            _transport.TotalRequests.ShouldBe(requests, "inspection must not acquire schema, refresh items, or flush drafts");
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(capture.Version);
            (await workspace.RetainReviewAsync(proposal, default)).IsSuccess.ShouldBeTrue();
            var retained = (await store.ReadAsync(connection, default)).Value;
            Run("plan", "status", "--file", file, "-o", "json").ShouldBe(0, stderr.ToString());
            using (var status = JsonDocument.Parse(stdout.ToString()))
            {
                status.RootElement.GetProperty("digest").GetString().ShouldBe(proposal.Digest);
                status.RootElement.GetProperty("allVerified").GetBoolean().ShouldBeFalse();
                status.RootElement.GetProperty("operations")[0].GetProperty("status").GetString().ShouldBe("Planned");
            }
            var started = retained.Journal.Single().Begin(Guid.NewGuid()).Value;
            var begun = await store.CommitAsync(connection, retained.Version, new CoreModel.StoreSnapshot(retained.Version + 1,
                retained.Items, retained.Relationships, retained.Drafts, retained.Proposals, [started],
                retained.Mappings, retained.Benches), default);
            begun.IsSuccess.ShouldBeTrue(begun.IsSuccess ? "" : begun.Error.Message);
            var applying = (await store.ReadAsync(connection, default)).Value;
            var unknown = started.Record(new CoreModel.Receipt(proposal.Digest, 0, started.Attempt,
                CoreModel.OpStatus.Indeterminate, ["No authoritative effect acknowledgment is available."],
                error: new CoreModel.Error(CoreModel.ErrorCode.Unavailable, "Original attempt remains unresolved."))).Value;
            var recorded = await store.CommitAsync(connection, applying.Version, new CoreModel.StoreSnapshot(applying.Version + 1,
                applying.Items, applying.Relationships, applying.Drafts, applying.Proposals, [unknown],
                applying.Mappings, applying.Benches), default);
            recorded.IsSuccess.ShouldBeTrue(recorded.IsSuccess ? "" : recorded.Error.Message);
            var fenced = (await store.ReadAsync(connection, default)).Value;
            Run("proposal", "status", "--file", file, "-o", "json").ShouldBe(0, stderr.ToString());
            using (var status = JsonDocument.Parse(stdout.ToString()))
            {
                var operation = status.RootElement.GetProperty("operations")[0];
                operation.GetProperty("status").GetString().ShouldBe("Indeterminate");
                operation.GetProperty("attempt").GetString().ShouldBe(started.Attempt.ToString("D"));
                operation.GetProperty("receipts")[0].GetProperty("error").GetProperty("message").GetString()
                    .ShouldBe("Original attempt remains unresolved.");
            }
            foreach (var format in new[] { "human", "minimal", "json-compact" })
            {
                Run("pending", "-o", format).ShouldBe(0, stderr.ToString());
                stdout.ToString().ShouldNotBeNullOrWhiteSpace();
                Run("proposal", "status", "--file", file, "-o", format).ShouldBe(0, stderr.ToString());
                Run("proposal", "validate", "--file", file, "-o", format).ShouldBe(0, stderr.ToString());
            }
            Run("proposal", "validate", "-o", "json").ShouldBe(2);
            Run("proposal", "status", "--file", Path.Combine(_temp, "outside.json"), "-o", "json").ShouldBe(2);
            Run("pending", "-o", "ids").ShouldBe(2);
            var foreign = new CoreModel.Proposal(new(connection.Provider, connection.ResourceScope, connection.Destination,
                "another principal"), proposal.SchemaFingerprint, proposal.ExpectedStoreVersion, proposal.Operations);
            var foreignFile = Path.Combine(paths.TwigDir, "foreign-inspection.json");
            await File.WriteAllBytesAsync(foreignFile, CoreModel.Core.ProposalBytes(foreign));
            Run("proposal", "status", "--file", foreignFile, "-o", "json").ShouldBe(2);
            using (var error = JsonDocument.Parse(stderr.ToString()))
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("another immutable workspace");
            var invalid = Path.Combine(paths.TwigDir, "old-inspection.json");
            await File.WriteAllTextAsync(invalid, """{"version":2}""");
            Run("proposal", "validate", "--file", invalid, "-o", "json").ShouldBe(2);
            var calls = telemetry.ReceivedCalls().ToArray();
            calls.ShouldNotBeEmpty();
            foreach (var call in calls)
            {
                var properties = ((Dictionary<string, string>)call.GetArguments()[1]!).ShouldNotBeNull();
                properties.Keys.ShouldAllBe(name => new[] { "command", "exit_code", "output_format", "twig_version", "os_platform", "operation_id" }.Contains(name));
                properties["command"].ShouldBeOneOf("pending", "proposal status", "proposal validate");
                properties.Values.ShouldNotContain(Actor);
                properties.Values.ShouldNotContain(file);
                properties.Values.ShouldNotContain("Captured edit");
            }
            telemetry.WhenForAnyArgs(client => client.TrackEvent(default!, default, default))
                .Do(_ => throw new InvalidOperationException("synthetic inspection telemetry failure"));
            Run("pending", "-o", "json").ShouldBe(0, stderr.ToString());
            using (var pending = JsonDocument.Parse(stdout.ToString()))
                pending.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
            _transport.TotalRequests.ShouldBe(requests);
            _transport.CommittedWrites.ShouldBe(0);
            var after = (await store.ReadAsync(connection, default)).Value;
            after.Version.ShouldBe(fenced.Version);
            after.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Indeterminate);
            var preserved = after.Drafts.Single(d => d.Key == seed.Key);
            preserved.Type.ShouldBe(seed.Type);
            preserved.Changes.Equals(seed.Changes).ShouldBeTrue();
            preserved.Parent.ShouldBe(seed.Parent);
            preserved.ParentRelation.ShouldBe(seed.ParentRelation);
            preserved.Resource.ShouldBe(seed.Resource);
            preserved.Links.ToArray().ShouldBe(seed.Links.ToArray());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreBenchPullKeepsDraftDebtAndExcludesImplicitMachineActiveContext()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        foreach (var id in new[] { 40, 42, 44, 45, 90, 91 })
            _transport.SetItem(id, "native " + id.ToString(CultureInfo.InvariantCulture), 1, Actor);
        _transport.SetHierarchy(40, 42);
        _transport.SetHierarchy(42, 44);
        _transport.SetHierarchy(90, 91);
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "scoped-pull-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["workspace", "sync", "-o", "json"]);
        using var root = services.BuildServiceProvider();
        root.GetRequiredService<TwigConfiguration>().Display.TreeDepthUp = 1;
        root.GetRequiredService<TwigConfiguration>().Display.TreeDepthDown = 1;
        root.GetRequiredService<TwigConfiguration>().Display.TreeDepthSideways = 0;
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
            (await commands.BenchCreate("Scoped pull", "json")).ShouldBe(0, stderr.ToString());
            (await commands.BenchSwitch("Scoped pull", "json")).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrackTree(42, "json")).ShouldBe(0, stderr.ToString());
            (await commands.Set("90", "human")).ShouldBe(0, stderr.ToString());
            var debt = (await admitted.Workspace.ReadAsync(CoreModel.ItemKey.Remote(connection.Provider,
                connection.ResourceScope, "45"), default)).Value;
            var local = CoreModel.ItemKey.Local(Guid.NewGuid());
            var changes = new CoreModel.FieldValues([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("local debt"))]);
            var state = (await store.ReadAsync(connection, default)).Value;
            (await store.CommitAsync(connection, state.Version, new(state.Version + 1, state.Items, state.Relationships,
                [new(debt.Key, debt.Type, debt, changes),
                    new(local, "Task", null, new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("seed debt")),
                        KeyValuePair.Create("System.State", CoreModel.FieldValue.String("To Do"))]))],
                state.Proposals, state.Journal, state.Mappings, state.Benches), default)).IsSuccess.ShouldBeTrue();
            _transport.SetItem(45, "new native debt value", 2, Actor);
            _transport.ItemReadIds.Clear();
            stdout.GetStringBuilder().Clear();
            (await commands.WorkspaceSync("json")).ShouldBe(0, stderr.ToString());
            using var pulled = JsonDocument.Parse(stdout.ToString());
            pulled.RootElement.GetProperty("itemCount").GetInt32().ShouldBe(4);
            pulled.RootElement.GetProperty("memberCount").GetInt32().ShouldBe(3);
            pulled.RootElement.GetProperty("itemsFlushed").GetInt32().ShouldBe(0);
            _transport.ItemReadIds.ShouldContain(40);
            _transport.ItemReadIds.ShouldContain(42);
            _transport.ItemReadIds.ShouldContain(44);
            _transport.ItemReadIds.ShouldContain(45);
            _transport.ItemReadIds.ShouldNotContain(90);
            _transport.ItemReadIds.ShouldNotContain(91);
            var retained = (await store.ReadAsync(connection, default)).Value;
            retained.Drafts.Single(draft => draft.Key == debt.Key).Baseline!.Fields["System.Title"].Text.ShouldBe("native 45");
            retained.Drafts.Single(draft => draft.Key == debt.Key).Changes.ShouldBe(changes);
            retained.Drafts.ShouldContain(draft => draft.Key == local);
            retained.Items.Single(item => item.Key == debt.Key).Fields["System.Title"].Text.ShouldBe("new native debt value");
            retained.Journal.ShouldBeEmpty();
            _transport.CommittedWrites.ShouldBe(0);
            _transport.ItemReadIds.Clear();
            stdout.GetStringBuilder().Clear();
            (await commands.Refresh("human")).ShouldBe(0, stderr.ToString());
            _transport.ItemReadIds.ShouldContain(90);
            _transport.ItemReadIds.ShouldContain(91);
            _transport.CommittedWrites.ShouldBe(0);
            _transport.ItemReadIds.Clear();
            var added = new CoreModel.Selector(CoreModel.SelectorKind.Pin,
                CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "90"), connection: connection);
            _transport.BeforeItemReadResponse = async () =>
            {
                var shared = UserBenchStore.ForRegistry(_bindings.RegistryPath);
                var selected = (await shared.ReadAsync()).Value.Benches.Single();
                (await shared.SaveAsync(new(selected.Id, selected.Name, selected.Selectors.Add(added)),
                    selected.Revision)).IsSuccess.ShouldBeTrue();
            };
            (await commands.WorkspaceSync("json")).ShouldBe(1, "a changed selector set cannot broaden the captured pull");
            (await admitted.Workspace.BenchSnapshotAsync(default)).Value.Benches.Single().Selectors.ShouldContain(added);
            _transport.ItemReadIds.ShouldNotContain(90);
            _transport.ItemReadIds.ShouldNotContain(91);
            _transport.CommittedWrites.ShouldBe(0);
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SyncCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.BenchSyncCommand").ShouldBeNull();
            AssertNativeDependencyClosure();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task CoreSyncUsesOneProposalAndOneJsonResultWithSafeOptionalTelemetry()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "sync-save-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        var telemetry = Substitute.For<ITelemetryClient>();
        services.AddSingleton(telemetry);
        services.AddTwigWorkspaceServices(paths.StartDir, ["sync", "-o", "json"]);
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
            (await commands.Sync("json")).ShouldBe(1);
            _transport.CommittedWrites.ShouldBe(0);
            (await commands.BenchCreate("Save then pull", "json")).ShouldBe(0, stderr.ToString());
            (await commands.BenchSwitch("Save then pull", "json")).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrack(42, "json")).ShouldBe(0, stderr.ToString());
            var state = (await store.ReadAsync(connection, default)).Value;
            var baseline = state.Items.Single(item => item.Key.NativeId == "42");
            var changes = new CoreModel.FieldValues([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("saved by sync"))]);
            (await store.CommitAsync(connection, state.Version, new(state.Version + 1, state.Items, state.Relationships,
                [new(baseline.Key, baseline.Type, baseline, changes)], state.Proposals, state.Journal,
                state.Mappings, state.Benches), default)).IsSuccess.ShouldBeTrue();
            (await commands.WorkspaceSync("json", expectBench: Guid.NewGuid().ToString("D"))).ShouldBe(1);
            _transport.CommittedWrites.ShouldBe(0);
            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            telemetry.ClearReceivedCalls();
            (await commands.Sync("json")).ShouldBe(0, stderr.ToString());
            using var result = JsonDocument.Parse(stdout.ToString());
            result.RootElement.GetProperty("itemsFlushed").GetInt32().ShouldBe(1);
            var digest = result.RootElement.GetProperty("digest").GetString().ShouldNotBeNull();
            var retained = (await store.ReadAsync(connection, default)).Value;
            retained.Journal.Single().Digest.ShouldBe(digest);
            retained.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Verified);
            retained.Drafts.ShouldBeEmpty();
            _transport.TitleOf(42).ShouldBe("saved by sync");
            _transport.CommittedWrites.ShouldBe(1);
            var call = telemetry.ReceivedCalls().ShouldHaveSingleItem();
            var properties = ((Dictionary<string, string>)call.GetArguments()[1]!).ShouldNotBeNull();
            properties["command"].ShouldBe("sync");
            properties["exit_code"].ShouldBe("0");
            properties["output_format"].ShouldBe("json");
            properties.Keys.ShouldAllBe(key => new[] { "command", "exit_code", "output_format", "twig_version", "os_platform", "operation_id" }.Contains(key));
            properties.Values.ShouldNotContain(Actor);
            properties.Values.ShouldNotContain("Save then pull");
            telemetry.WhenForAnyArgs(client => client.TrackEvent(default!, default, default))
                .Do(_ => throw new InvalidOperationException("synthetic telemetry failure"));
            stdout.GetStringBuilder().Clear();
            (await commands.Sync("json", pullOnly: true)).ShouldBe(0, stderr.ToString());
            using var repeated = JsonDocument.Parse(stdout.ToString());
            repeated.RootElement.GetProperty("itemsFlushed").GetInt32().ShouldBe(0);
            _transport.CommittedWrites.ShouldBe(1);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
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
            var created = (await admitted.Workspace.BenchSnapshotAsync(default)).Value.Benches.ShouldHaveSingleItem();
            (await store.ReadAsync(connection, default)).Value.Benches.ShouldBeEmpty();
            (await admitted.Workspace.Context.ReadAsync(default)).Value.BenchId.ShouldBeNull();
            (await commands.BenchCreate("review SCOPE", "json")).ShouldBe(1);
            (await commands.BenchSwitch("missing scope", "json")).ShouldBe(1);
            (await commands.BenchSwitch("Review scope", "json", expectBench: created.Id.ToString("D"))).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrack(42, "json", expectBench: created.Id.ToString("D"))).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrackTree(42, "json")).ShouldBe(0, stderr.ToString());
            var snapshot = (await admitted.Workspace.BenchSnapshotAsync(default)).Value;
            snapshot.Benches.Single().Selectors.Select(selector => selector.Kind).ShouldBe([CoreModel.SelectorKind.Pin, CoreModel.SelectorKind.Subtree]);
            var query = new CoreModel.Selector(CoreModel.SelectorKind.Query, query: new(CoreModel.QueryKind.All));
            (await admitted.Workspace.CaptureBenchesAsync(snapshot,
                [new(created.Id, created.Name, snapshot.Benches.Single().Selectors.Add(query))], default)).IsSuccess.ShouldBeTrue();
            (await commands.WorkspaceUntrack(42, "json", mode: "SiNgLe")).ShouldBe(0, stderr.ToString());
            var unpinned = (await admitted.Workspace.BenchSnapshotAsync(default)).Value.Benches.Single();
            unpinned.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Query);
            unpinned.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Subtree);
            unpinned.Selectors.ShouldNotContain(selector => selector.Kind == CoreModel.SelectorKind.Pin);
            _transport.CoreMetadataEnabled = false;
            (await commands.WorkspaceUntrack(42, "json", mode: "TrEe")).ShouldBe(0, stderr.ToString());
            _transport.CoreMetadataEnabled = true;
            var final = (await admitted.Workspace.BenchSnapshotAsync(default)).Value;
            final.Benches.Single().Selectors.ShouldHaveSingleItem().Kind.ShouldBe(CoreModel.SelectorKind.Query);
            var version = final.Version;
            (await commands.WorkspaceTrack(43, "json", expectBench: Guid.NewGuid().ToString("D"))).ShouldBe(1);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(version);
            (await commands.WorkspaceTrack(42, "json")).ShouldBe(0, stderr.ToString());
            (await commands.WorkspaceTrackTree(42, "json")).ShouldBe(0, stderr.ToString());
            (await admitted.Workspace.BenchSnapshotAsync(default)).Value.Benches.Single().Selectors.Count(selector =>
                selector.Kind is CoreModel.SelectorKind.Pin or CoreModel.SelectorKind.Subtree).ShouldBe(2);
            _transport.CoreMetadataEnabled = false;
            (await commands.WorkspaceUntrack(42, "json")).ShouldBe(0, stderr.ToString());
            _transport.CoreMetadataEnabled = true;
            var removed = (await admitted.Workspace.BenchSnapshotAsync(default)).Value;
            removed.Benches.Single().Selectors.ShouldHaveSingleItem().Kind.ShouldBe(CoreModel.SelectorKind.Query);
            (await commands.WorkspaceUntrack(42, "json")).ShouldBe(0, stderr.ToString());
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(removed.Version);
            var sharedBeforeExclusion = (await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadAsync()).Value.Benches.Single();
            var requestCount = _transport.TotalRequests;
            RunCoreLink(paths, ["workspace", "exclude", "42", "-o", "json"]).ShouldBe(0, stderr.ToString());
            _transport.TotalRequests.ShouldBe(requestCount, "view exclusions must not perform work HTTP");
            stdout.GetStringBuilder().Clear();
            (await commands.Workspace("json")).ShouldBe(0, stderr.ToString());
            using (var view = JsonDocument.Parse(stdout.ToString()))
                view.RootElement.GetProperty("items").EnumerateArray().ShouldNotContain(row => row.GetProperty("id").GetInt32() == 42);
            var sharedAfterExclusion = (await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadAsync()).Value.Benches.Single();
            Twig.Store.Sqlite.BenchCodec.Serialize(sharedAfterExclusion).ShouldBe(Twig.Store.Sqlite.BenchCodec.Serialize(sharedBeforeExclusion));
            RunCoreLink(paths, ["workspace", "exclusions", "--clear", "-o", "json"]).ShouldBe(0, stderr.ToString());

            var concurrentQuery = new CoreModel.Selector(CoreModel.SelectorKind.Query,
                query: new(CoreModel.QueryKind.Equal, "System.Title", CoreModel.FieldValue.String("concurrent selector")));
            _transport.SetItem(44, "new native pin target", 1, Actor);
            _transport.BeforeItemReadResponse = async () =>
            {
                var shared = UserBenchStore.ForRegistry(_bindings.RegistryPath);
                var bench = (await shared.ReadAsync()).Value.Benches.Single();
                var scoped = new CoreModel.Selector(concurrentQuery.Kind, query: concurrentQuery.Query, connection: connection);
                var changed = new CoreModel.Bench(bench.Id, bench.Name, bench.Selectors.Add(scoped));
                (await shared.SaveAsync(changed, bench.Revision)).IsSuccess.ShouldBeTrue();
            };
            (await commands.WorkspaceTrack(44, "json")).ShouldBe(0, stderr.ToString());
            var afterRace = (await admitted.Workspace.BenchSnapshotAsync(default)).Value.Benches.Single();
            afterRace.Selectors.ShouldContain(selector => selector.Kind == CoreModel.SelectorKind.Query
                && selector.Query!.Field == concurrentQuery.Query!.Field && selector.Query.Value!.Equals(concurrentQuery.Query.Value));
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
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.BenchCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.TrackingCommand").ShouldBeNull();
            AssertNativeDependencyClosure();
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
            var beforeRetiredHistory = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
            (await commands.NavBack("human")).ShouldBe(1, "history movement is deliberately retired, not a missing service");
            (await commands.NavFore("human")).ShouldBe(1);
            (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(beforeRetiredHistory);
            var returned = (await admitted.Workspace.Context.ReadAsync(default)).Value;
            returned.History[returned.Position].NativeId.ShouldBe("42");
            stdout.GetStringBuilder().Clear();
            var beforeHistory = _transport.WorkRequests;
            (await commands.NavHistory(nonInteractive: true, output: "json")).ShouldBe(1);
            stdout.ToString().ShouldBeEmpty();
            (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(beforeRetiredHistory);
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
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.EditCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SetCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.NavigationCommands").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.NavigationHistoryCommands").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.QueryCommand").ShouldBeNull();
            AssertNativeDependencyClosure();
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
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.QueryCommand").ShouldBeNull();
            AssertNativeDependencyClosure();
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
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedChainCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedLinkRepairCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedValidateCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedEditCommand").ShouldBeNull();
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
            AssertNativeDependencyClosure();
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
            var requestsBeforeLinkExport = _transport.TotalRequests;
            stdout.GetStringBuilder().Clear();
            (await commands.SeedLinks(id: aliases[1], output: "json")).ShouldBe(0, stderr.ToString());
            using var middleLinks = JsonDocument.Parse(stdout.ToString());
            middleLinks.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
            middleLinks.RootElement.GetProperty("links").EnumerateArray().Select(link => (
                link.GetProperty("sourceId").GetInt32(), link.GetProperty("targetId").GetInt32())).Order()
                .ShouldBe(new[] { (aliases[0], aliases[1]), (aliases[1], aliases[2]) }.Order());
            _transport.TotalRequests.ShouldBe(requestsBeforeLinkExport,
                "both directions of retained creation-link capture export must be cache-only");
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
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedLinkCommand").ShouldBeNull();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedPublishCommand").ShouldBeNull();
            AssertNativeDependencyClosure();
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
        typeof(TwigCommands).Assembly.GetType("Twig.Commands.SeedPublishCommand").ShouldBeNull();
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
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        AssertNativeRegistrations(services);
        using var root = services.BuildServiceProvider();
        typeof(TwigCommands).Assembly.GetType("Twig.Commands.IPendingChangeFlusher").ShouldBeNull();
        AssertNativeDependencyClosure();
        foreach (var deleted in new[] { "UpdateCommand", "SaveCommand", "DiscardCommand" })
            typeof(TwigCommands).Assembly.GetType("Twig.Commands." + deleted).ShouldBeNull(
                "remaining legacy CLI ownership must be deleted, not merely bypassed");
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        (await admitted.Workspace.StageEditAsync(key,
            new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Explicit saved draft"))]), default)).IsSuccess.ShouldBeTrue();
        (await store.ReadAsync(connection, default)).Value.Drafts.ShouldHaveSingleItem();
        var seed = (await admitted.Workspace.StageSeedAsync("Fixture.Task",
            new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Preserved local draft"))]), default)).Value;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int Run(params string[] args)
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                return Twig.TwigConsoleApp.Run(args, registration =>
                {
                    registration.AddSingleton<IConnectionBindingService>(_bindings);
                    registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                    registration.AddTwigWorkspaceServices(paths.StartDir, args);
                });
            }
            Run("save", "--all", "--output", "json").ShouldBe(0, stderr.ToString());
            var saved = (await store.ReadAsync(connection, default)).Value;
            saved.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seed.Key);
            saved.Drafts.Single().Changes.ShouldBe(seed.Changes);
            saved.Journal.ShouldHaveSingleItem().Status.ShouldBe(CoreModel.OpStatus.Verified);
            using (var result = JsonDocument.Parse(stdout.ToString()))
                saved.Proposals.Single().Digest.ShouldBe(result.RootElement.GetProperty("digest").GetString());
            saved.Items.Single().Fields["System.Title"].Text.ShouldBe("Explicit saved draft");
            _transport.TitleOf(42).ShouldBe("Explicit saved draft");
            _transport.CommittedWrites.ShouldBe(1);
            (await admitted.Workspace.StageEditAsync(key,
                new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Never publish discarded draft"))]), default))
                .IsSuccess.ShouldBeTrue();
            var beforeDiscard = (await store.ReadAsync(connection, default)).Value;
            beforeDiscard.Drafts.Length.ShouldBe(2);
            var requests = _transport.TotalRequests;
            Run("discard", "42", "--yes", "-o", "json").ShouldBe(0, stderr.ToString());
            var discarded = (await store.ReadAsync(connection, default)).Value;
            discarded.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seed.Key);
            discarded.Drafts.Single().Changes.ShouldBe(seed.Changes);
            discarded.Journal.ShouldHaveSingleItem().Status.ShouldBe(CoreModel.OpStatus.Verified);
            discarded.Journal.Single().Digest.ShouldBe(saved.Journal.Single().Digest);
            discarded.Proposals.Single().Digest.ShouldBe(saved.Proposals.Single().Digest);
            _transport.TotalRequests.ShouldBe(requests, "discard is a local CAS, not a native refresh or publication");
            _transport.TitleOf(42).ShouldBe("Explicit saved draft");
            _transport.CommittedWrites.ShouldBe(1);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData(false, "json", false)]
    [InlineData(true, "json", false)]
    [InlineData(false, "json-full", true)]
    [InlineData(false, "json-compact", false)]
    [InlineData(false, "minimal", false)]
    [InlineData(false, "human", false)]
    public async Task CoreInitCliCreatesAnAdmittedWorkspaceAndRefusesReinitializationWithoutChangingIt(
        bool explicitStore, string format, bool explicitBinding)
    {
        await AttachAsync();
        var root = Path.Combine(_temp, "fresh-core-checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "# retained custom ignores\n/.twig/\n");
        var ignore = await File.ReadAllBytesAsync(Path.Combine(root, ".gitignore"));
        var selected = await _bindings.CreateBindingAsync("fixture", "Project", "sibling", false);
        var policy = Path.Combine(root, "twig.policy.json");
        await File.WriteAllTextAsync(policy, """{"version":1,"rules":[]}""");
        var policyBytes = await File.ReadAllBytesAsync(policy);
        byte[]? manifestBytes = null;
        if (explicitBinding)
        {
            await new TwigConfiguration { Organization = "fixture", Project = "Project" }
                .SaveRepoAsync(Path.Combine(root, "twig.json"));
            manifestBytes = await File.ReadAllBytesAsync(Path.Combine(root, "twig.json"));
        }
        _transport.CoreMetadataEnabled = true;
        _transport.ObservedPaths.Clear();
        var database = explicitStore ? Path.Combine(root, "explicit-core.db") : Path.Combine(root, ".twig", "core.db");
        var arguments = new[] { "init", "fixture", "Project", "-o", format }
            .Concat(explicitStore ? ["--core-store", database] : Array.Empty<string>())
            .Concat(explicitBinding ? ["--binding", selected.BindingId] : Array.Empty<string>()).ToArray();
        var requests = _transport.WorkRequests;
        IServiceCollection? registrations = null;
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
            services.AddSingleton(_systemRegistry);
            registrations = services;
        });
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Run().ShouldBe(0, stderr.ToString());
            Directory.Exists(Path.Combine(root, ".twig", "cache")).ShouldBeFalse("native init must never initialize the legacy cache");
            registrations.ShouldNotBeNull();
            AssertNativeRegistrations(registrations);
            _transport.WorkRequests.ShouldBe(requests, "init only observes native identity/project metadata");
            _transport.ObservedPaths.ShouldNotBeEmpty();
            _transport.ObservedPaths.ShouldAllBe(path => path == "/fixture/_apis/connectionData" || path == "/fixture/_apis/projects/Project");
            if (format.StartsWith("json", StringComparison.Ordinal))
            {
                using var output = JsonDocument.Parse(stdout.ToString());
                output.RootElement.GetProperty("coreInitialized").GetBoolean().ShouldBeTrue();
                output.RootElement.GetProperty("databasePath").GetString().ShouldBe(database);
            }
            else stdout.ToString().ShouldContain(database);
            (await File.ReadAllBytesAsync(Path.Combine(root, ".gitignore"))).ShouldBe(ignore);
            InitCommandTestFixture.IsIgnored(root, Path.Combine(".twig", "core.db")).ShouldBeTrue();
            InitCommandTestFixture.IsIgnored(root, Path.Combine(".twig", "core.db-wal")).ShouldBeTrue();
            if (explicitStore) stdout.ToString().ShouldContain("not automatically Git-ignored");
            (await File.ReadAllBytesAsync(policy)).ShouldBe(policyBytes);
            if (manifestBytes is not null)
                (await File.ReadAllBytesAsync(Path.Combine(root, "twig.json"))).ShouldBe(manifestBytes);
            await using var marker = File.OpenRead(Path.Combine(root, ".twig", CoreWorkspaceFactory.FileName));
            var document = (await JsonSerializer.DeserializeAsync(marker,
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument))!;
            document.PrincipalId.ShouldBe(explicitBinding ? Sibling : Actor);
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
            if (format.StartsWith("json", StringComparison.Ordinal))
            {
                using var refusal = JsonDocument.Parse(stderr.ToString());
                refusal.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("existing");
            }
            else stderr.ToString().ShouldContain("existing");
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

    [Theory]
    [InlineData("unknown-binding")]
    [InlineData("wrong-endpoint")]
    [InlineData("wrong-principal")]
    [InlineData("changed-binding")]
    public async Task CoreInitNativeAuthorityFailureNeverCreatesAStoreOrReusesPartialEvidence(string defect)
    {
        await AttachAsync();
        var root = Path.Combine(_temp, "refused-native-init");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        var selected = await _bindings.CreateBindingAsync("fixture", "OtherProject", "actor", false);
        var sibling = await _bindings.CreateBindingAsync("fixture", "Project", "sibling", false);
        var args = new[] { "init", "fixture", "Project", "-o", "json" }
            .Concat(defect is "unknown-binding" or "wrong-endpoint"
                ? new[] { "--binding", defect == "unknown-binding" ? "bind-unknown" : selected.BindingId }
                : Array.Empty<string>()).ToArray();
        _transport.CoreMetadataEnabled = true;
        if (defect == "wrong-principal") _transport.ObservedPrincipalOverride = Sibling;
        if (defect == "changed-binding")
            _transport.BeforeProjectMetadata = async () =>
            {
                var attachment = Path.Combine(root, ".twig", "attachment.json");
                var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(attachment))!;
                json["bindingPin"] = sibling.BindingId;
                await File.WriteAllTextAsync(attachment, json.ToJsonString());
            };
        var workRequests = _transport.WorkRequests;
        var requests = _transport.TotalRequests;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        int Run() => Twig.TwigConsoleApp.Run(args, services =>
        {
            services.AddSingleton<IConnectionBindingService>(_bindings);
            services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
            services.AddTwigWorkspaceServices(root, args, Path.Combine(Home, "display.json"));
        });
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Run().ShouldBe(1, stderr.ToString());
            stdout.ToString().ShouldBeEmpty();
            using var error = JsonDocument.Parse(stderr.ToString());
            error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            File.Exists(Path.Combine(root, ".twig", "core.db")).ShouldBeFalse();
            File.Exists(Path.Combine(root, ".twig", CoreWorkspaceFactory.FileName)).ShouldBeFalse();
            Directory.Exists(Path.Combine(root, ".twig", "cache")).ShouldBeFalse();
            _transport.WorkRequests.ShouldBe(workRequests);
            _transport.CommittedWrites.ShouldBe(0);
            if (defect is "unknown-binding" or "wrong-endpoint")
            {
                Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
                _transport.TotalRequests.ShouldBe(requests);
            }
            else
            {
                File.Exists(Path.Combine(root, ".twig", "attachment.json")).ShouldBeTrue();
                var files = Directory.GetFiles(Path.Combine(root, ".twig"), "*", SearchOption.AllDirectories)
                    .ToDictionary(file => file, File.ReadAllBytes);
                var refusedRequests = _transport.TotalRequests;
                stderr.GetStringBuilder().Clear();
                Run().ShouldBe(1);
                stderr.ToString().ShouldContain("existing");
                _transport.TotalRequests.ShouldBe(refusedRequests);
                foreach (var (file, bytes) in files) (await File.ReadAllBytesAsync(file)).ShouldBe(bytes);
            }
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    [Fact]
    public async Task LegacyWorkspaceMutationIsRefusedBeforeProductionWriterRegistration()
    {
        var legacy = await AttachAsync();
        var paths = legacy.GetRequiredService<TwigPaths>();
        var requests = _transport.WorkRequests;
        foreach (var verb in new[] { "update", "state", "note", "patch", "save", "discard" })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnectionBindingService>(_bindings);
            var failure = Should.Throw<InvalidOperationException>(() =>
                services.AddTwigWorkspaceServices(paths.StartDir, [verb, "--id", "42", "-o", "json"]));
            failure.Message.ShouldContain("Core workspace");
            AssertNativeRegistrations(services);
        }
        _transport.WorkRequests.ShouldBe(requests);
        _transport.CommittedWrites.ShouldBe(0);
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
            AssertNativeDependencyClosure();
            typeof(TwigCommands).Assembly.GetType("Twig.Commands.IPendingChangeFlusher").ShouldBeNull();
            foreach (var deleted in new[] { "StateCommand", "NoteCommand", "PatchCommand", "UpdateCommand", "SaveCommand", "DiscardCommand" })
                typeof(TwigCommands).Assembly.GetType("Twig.Commands." + deleted).ShouldBeNull(
                    "the old command ownership must be absent, not merely unregistered in this graph");
            int Run(params string[] args) => Twig.TwigConsoleApp.Run(args, registration =>
            {
                registration.AddSingleton<IConnectionBindingService>(_bindings);
                registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                registration.AddTwigWorkspaceServices(paths.StartDir, args, Path.Combine(Home, "display.json"));
            });
            stdout.GetStringBuilder().Clear();
            var show = new TwigCommands(root);
            var requestsBeforeCachedRead = _transport.TotalRequests;
            (await show.Show(42, "json")).ShouldBe(0, stderr.ToString());
            using var read = JsonDocument.Parse(stdout.ToString());
            read.RootElement.GetProperty("title").GetString().ShouldBe("Core production title");
            read.RootElement.GetProperty("cached").GetBoolean().ShouldBeTrue();
            _transport.TotalRequests.ShouldBe(requestsBeforeCachedRead, "a cached show must not acquire schema or work HTTP");
            _transport.CommittedWrites.ShouldBe(1, "show must not flush unrelated pending state");
            foreach (var args in new[]
            {
                new[] { "show", "42", "--sections", "links", "-o", "json" },
                new[] { "show-batch", "42", "--sections", "links", "-o", "json" },
            })
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                Run(args).ShouldBe(1, string.Join(" ", args) + ": " + stderr);
                stdout.ToString().ShouldBeEmpty("retired section envelopes must not pretend to export complete links");
                using var refusal = JsonDocument.Parse(stderr.ToString());
                refusal.RootElement.GetProperty("error").GetString().ShouldNotBeNull()
                    .Contains("section", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
                _transport.TotalRequests.ShouldBe(requestsBeforeCachedRead);
                (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(snapshot.Version);
            }
            stdout.GetStringBuilder().Clear();
            Run("set", "42").ShouldBe(0, stderr.ToString());
            stdout.GetStringBuilder().Clear();
            var beforeStates = _transport.WorkRequests;
            Run("states").ShouldBe(0, stderr.ToString());
            stdout.ToString().ShouldContain("Fixture.Task");
            _transport.WorkRequests.ShouldBe(beforeStates, "human states resolves the cached Core active type, not a work query");
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
            Run("note", "--text", "Core root note", "--id", "42", "--format", "raw", "-o", "json").ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(3);
            stdout.GetStringBuilder().Clear();
            (await show.LinkArtifact("https://example.test/root-evidence", "Root evidence", 42, "json")).ShouldBe(0, stderr.ToString());
            _transport.CommittedWrites.ShouldBe(4);
            var final = (await store.ReadAsync(connection, default)).Value;
            final.Journal.ShouldAllBe(row => row.Status == CoreModel.OpStatus.Verified);
            final.Items.Single().Resources.ShouldContain(resource => resource.Target == "https://example.test/root-evidence");
            AssertNativeDependencyClosure();
            stdout.GetStringBuilder().Clear();
            Run("state", "Doing", "--id", "42", "-o", "json").ShouldBe(0, stderr.ToString());
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
            Run("patch", "--id", "42", "--json", """{"System.Title":"Typed rooted patch"}""", "-o", "json").ShouldBe(0, stderr.ToString());
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
            Run("save", "--all", "--output", "json").ShouldBe(0, stderr.ToString());
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
            var requestsBeforeDiscard = _transport.TotalRequests;
            Run("discard", "42", "-o", "json").ShouldBe(2);
            (await store.ReadAsync(connection, default)).Value.Drafts.Length.ShouldBe(2);
            stdout.GetStringBuilder().Clear();
            Run("discard", "42", "--yes", "-o", "json").ShouldBe(0, stderr.ToString());
            _transport.TotalRequests.ShouldBe(requestsBeforeDiscard);
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
        using var inspection = new ConnectionRemoteWriteReconciliationService(Home, _bindings);
        var unknown = (await inspection.InspectAsync(config, paths)).ShouldHaveSingleItem();
        unknown.State.ShouldBe("unknown");
        unknown.CanReconcile.ShouldBeFalse();
        unknown.Request.ShouldBe(recorded.Intent.Request);
        unknown.Digest.ShouldBe(recorded.Intent.RequestDigest);
        unknown.Receipt.ShouldBeNull();
        var afterInspection = (await _systemRegistry.ReadConnectionRemoteWritesAsync(origin.Operation.WorktreeFingerprint))
            .Value.ShouldHaveSingleItem();
        afterInspection.Intent.ShouldBe(recorded.Intent, "inspection must not alter the original attempt");
        afterInspection.Receipt.ShouldBeNull();
        afterInspection.Observations.ShouldBeEmpty();
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
        var acknowledged = (await inspection.InspectAsync(config, paths)).ShouldHaveSingleItem();
        acknowledged.Receipt.ShouldBe(outcome.Receipt);
        acknowledged.Observations.ShouldBe(outcome.Observations);
        acknowledged.CanReconcile.ShouldBeFalse();
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

    private async Task<ServiceProvider> AttachAsync()
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
        return BuildRuntime(config, paths);
    }

    private ServiceProvider BuildRuntime(TwigConfiguration config, TwigPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddCoreWorkspaceServices(config, paths);
        services.AddSingleton(_systemRegistry);
        services.AddSingleton(_http);
        services.AddTwigRenderingServices();
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        return runtime;
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
        internal const string SavedQuery = "77777777-7777-7777-7777-777777777777";
        internal int[] SavedQueryItems { get; set; } = [42];
        internal Func<Task>? BeforePlanningBatchResponse { get; set; }
        private readonly Dictionary<int, Item> _items = new();
        private readonly Dictionary<(int Id, int Revision), Item> _history = new();
        private readonly Dictionary<int, List<(string Relation, int Target)>> _links = new();
        private readonly Dictionary<(int Id, int Revision), (string Relation, int Target)[]> _linkHistory = new();
        public int WorkRequests { get; private set; }
        public int TotalRequests { get; private set; }
        public List<string> ObservedPaths { get; } = [];
        public int PostCount { get; private set; }
        public int CommittedWrites { get; private set; }
        public int PatchAttempts { get; private set; }
        public int ReadbacksAfterCommit { get; private set; }
        public List<int> QueryAfterIds { get; } = [];
        public List<int> ItemReadIds { get; } = [];
        public bool RejectNextPatch { get; set; }
        public bool FailReadbackAfterCommit { get; set; }
        public Func<Task>? BeforeReadback { get; set; }
        public Func<Task>? BeforeItemReadResponse { get; set; }
        public Func<Task>? AfterCommit { get; set; }
        public bool CoreMetadataEnabled { get; set; }
        public string? ObservedPrincipalOverride { get; set; }
        public Func<Task>? BeforeProjectMetadata { get; set; }
        public bool CloseGateEnabled { get; set; }
        public bool FailSchema { get; set; }
        public HttpStatusCode? SchemaFailureStatus { get; set; }
        public int SourceHandlesDisposed { get; private set; }
        public bool LoseNextPatchAcknowledgement { get; set; }
        public List<(int Id, string Body)> RelationshipPatches { get; } = [];
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(PrincipalTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => owner.SendAsync(request, ct);
            protected override void Dispose(bool disposing)
            {
                if (disposing) owner.SourceHandlesDisposed++;
                base.Dispose(disposing);
            }
        }
        public string TitleOf(int id) => _items[id].Title;
        public void ForgetAssociation(int source, int target)
        {
            _links[source].Remove(("Native.Association", target));
            _links[target].Remove(("Native.Association", source));
        }
        public int RevisionOf(int id) => _items[id].Revision;
        public void SetItem(int id, string title, int revision, string principal)
        {
            var prior = _items.GetValueOrDefault(id);
            var item = new Item(title, revision, principal, prior?.History ?? "", prior?.Resources ?? [], prior?.State ?? "To Do");
            _items[id] = item;
            _history[(id, revision)] = item;
            _linkHistory[(id, revision)] = _links.GetValueOrDefault(id, []).ToArray();
        }
        public void SetHierarchy(int parent, int child)
        {
            if (!_links.TryGetValue(parent, out var children)) _links[parent] = children = [];
            if (!_links.TryGetValue(child, out var parents)) _links[child] = parents = [];
            children.Add(("Native.ParentForward", child));
            parents.Add(("Native.ParentReverse", parent));
            _linkHistory[(parent, _items[parent].Revision)] = children.ToArray();
            _linkHistory[(child, _items[child].Revision)] = parents.ToArray();
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
            ObservedPaths.Add(path);
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new
                {
                    instanceId = "11111111-1111-1111-1111-111111111111",
                    authenticatedUser = new { id = ObservedPrincipalOverride ?? principal, providerDisplayName = "Same display" }
                });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Same display", emailAddress = principal + "@fixture.example" });
            if (FailSchema && path.EndsWith("/fields", StringComparison.Ordinal))
                return Reply(SchemaFailureStatus ?? HttpStatusCode.BadRequest, new { message = "Fixture schema unavailable" });
            if (CoreMetadataEnabled && path.Contains("/teams/", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { id = "88888888-8888-8888-8888-888888888888",
                    name = Uri.UnescapeDataString(path.Split('/')[^1]) });
            if (CoreMetadataEnabled && path.EndsWith("/_apis/wit/queries", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { count = 1, value = new[] {
                    new { id = SavedQuery, name = "Fixture saved query", path = "Shared/Fixture query", isFolder = false,
                        queryType = "flat", wiql = "SELECT [System.Id] FROM WorkItems" } } });
            if (CoreMetadataEnabled && path.EndsWith("/_apis/work/backlogs", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
            if (CoreMetadataEnabled && path.EndsWith("/_apis/wit/wiql/" + SavedQuery, StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { queryType = "flat", asOf = DateTimeOffset.UtcNow,
                    workItems = SavedQueryItems.Select(id => new { id }).ToArray() });
            if (CoreMetadataEnabled && path.EndsWith("/_apis/wit/workitemsbatch", StringComparison.Ordinal))
            {
                request.Method.ShouldBe(HttpMethod.Post);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body.RootElement.GetProperty("$expand").GetString().ShouldBe("relations");
                body.RootElement.GetProperty("errorPolicy").GetString().ShouldBe("fail");
                var ids = body.RootElement.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (ids.Any(id => !_items.TryGetValue(id, out var item) || item.Principal != principal))
                    return Reply(HttpStatusCode.Forbidden, new { message = "Fixture native membership is outside this principal" });
                if (BeforePlanningBatchResponse is { } beforeBatch)
                { BeforePlanningBatchResponse = null; await beforeBatch(); }
                ItemReadIds.AddRange(ids);
                return Reply(HttpStatusCode.OK, new { count = ids.Length, value = ids.Select(id => ItemData(id, _items[id])).ToArray() });
            }
            if (CoreMetadataEnabled && Metadata(path, request.RequestUri.Query) is { } metadata)
            {
                if (path.Contains("/_apis/projects/", StringComparison.Ordinal) && BeforeProjectMetadata is { } before)
                    await before();
                return metadata;
            }
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
                _linkHistory[(createdId, 1)] = _links.GetValueOrDefault(createdId, []).ToArray();
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
                var patchBody = await request.Content!.ReadAsStringAsync(ct);
                using var operations = JsonDocument.Parse(patchBody);
                if (operations.RootElement.EnumerateArray().Any(operation =>
                    operation.GetProperty("path").GetString()!.StartsWith("/relations/", StringComparison.Ordinal)))
                    RelationshipPatches.Add((id, patchBody));
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
                        var relation = value.GetProperty("rel").GetString()!;
                        if (relation is "Native.ParentForward" or "Native.DependencyForward" or "Native.Association")
                        {
                            var endpoint = int.Parse(new Uri(value.GetProperty("url").GetString()!).Segments[^1], CultureInfo.InvariantCulture);
                            _items[endpoint].Principal.ShouldBe(principal);
                            if (!_links.TryGetValue(id, out var links)) _links[id] = links = [];
                            if (links.Contains((relation, endpoint)))
                                return Reply(HttpStatusCode.Conflict, new { message = "Duplicate relationship" });
                            links.Add((relation, endpoint));
                            if (!_links.TryGetValue(endpoint, out var reciprocal)) _links[endpoint] = reciprocal = [];
                            reciprocal.Add((Inverse(relation), id));
                            SetItem(endpoint, _items[endpoint].Title, _items[endpoint].Revision + 1, principal);
                            continue;
                        }
                        var attributes = value.TryGetProperty("attributes", out var linkAttributes) ? linkAttributes : default;
                        resources.Add(new(value.GetProperty("rel").GetString()!, value.GetProperty("url").GetString()!,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("name", out var name) ? name.GetString() : null,
                            attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("comment", out var comment) ? comment.GetString() : null));
                    }
                    else if (target!.StartsWith("/relations/", StringComparison.Ordinal)
                        && operation.GetProperty("op").GetString() == "remove")
                    {
                        var index = int.Parse(target["/relations/".Length..], CultureInfo.InvariantCulture) - resources.Count;
                        var links = _links[id];
                        var removed = links[index];
                        links.RemoveAt(index);
                        _links[removed.Target].Remove((Inverse(removed.Relation), id));
                        var endpoint = _items[removed.Target];
                        SetItem(removed.Target, endpoint.Title, endpoint.Revision + 1, principal);
                    }
                }
                SetItem(id, title, item.Revision + 1, item.Principal);
                _items[id] = _items[id] with { History = history, Resources = resources.ToArray(), State = state };
                _history[(id, _items[id].Revision)] = _items[id];
                _linkHistory[(id, _items[id].Revision)] = _links.GetValueOrDefault(id, []).ToArray();
                item = _items[id];
                CommittedWrites++;
                if (AfterCommit is { } afterCommit)
                {
                    AfterCommit = null;
                    await afterCommit();
                }
                if (LoseNextPatchAcknowledgement)
                {
                    LoseNextPatchAcknowledgement = false;
                    throw new HttpRequestException("Fixture lost acknowledgement after commit");
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
            if (request.Method == HttpMethod.Get) ItemReadIds.Add(id);
            if (request.Method == HttpMethod.Get && CommittedWrites > 0)
            {
                ReadbacksAfterCommit++;
                if (BeforeReadback is { } beforeReadback)
                {
                    BeforeReadback = null;
                    await beforeReadback();
                }
            }
            return Reply(HttpStatusCode.OK, ItemData(id, item, segments.Length == 3 && segments[1] == "revisions"
                ? int.Parse(segments[2], CultureInfo.InvariantCulture) : null));
        }

        private object ItemData(int id, Item item, int? revision = null) => new
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
                }).Cast<object>().Concat(NativeLinks(id, revision)).ToArray()
            };

        private static string Inverse(string relation) => relation switch
        {
            "Native.ParentForward" => "Native.ParentReverse", "Native.ParentReverse" => "Native.ParentForward",
            "Native.DependencyForward" => "Native.DependencyReverse", "Native.DependencyReverse" => "Native.DependencyForward",
            "Native.Association" => "Native.Association", _ => throw new InvalidOperationException("Unknown fixture relationship"),
        };

        private object[] NativeLinks(int id, int? revision = null) => (revision.HasValue
            ? _linkHistory.GetValueOrDefault((id, revision.Value), [])
            : _links.GetValueOrDefault(id, []).ToArray())
            .Select(link => (object)new
            {
                rel = link.Relation,
                url = "https://dev.azure.com/fixture/_apis/wit/workItems/" + link.Target.ToString(CultureInfo.InvariantCulture),
                attributes = new { },
            }).ToArray();

        private HttpResponseMessage? Metadata(string path, string query)
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
                    "System.AreaPath", "System.IterationPath", "System.AssignedTo", "System.History" }
                    .Concat(CloseGateEnabled ? ["Fixture.CloseEvidence"] : Array.Empty<string>());
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
            if (CloseGateEnabled && path.EndsWith("/rules", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = new[] { new
                {
                    conditions = new[] { new { conditionType = "$when", field = "System.State", value = "Done" } },
                    actions = new[] { new { actionType = "$makeRequired", targetField = "Fixture.CloseEvidence" } },
                    isDisabled = false
                } } });
            if (path.EndsWith("/layout", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new
                {
                    pages = Array.Empty<object>(),
                    systemControls = new[] { new { id = "System.Title", label = "Title", controlType = "FieldControl" } },
                });
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
