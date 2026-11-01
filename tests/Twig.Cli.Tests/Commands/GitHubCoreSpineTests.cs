using Twig.Infrastructure.Telemetry;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshInitUsesOnlyNativeCoreBootstrap(bool explicitStore)
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Root, ".gitignore"), "# preserve this existing content\n");
        await f.Initialize(explicitStore);
        (await File.ReadAllTextAsync(Path.Combine(f.Root, ".gitignore"))).ShouldStartWith("# preserve this existing content\n");
        InitCommandTestFixture.IsIgnored(f.Root, Path.Combine(".twig", "core.db")).ShouldBeTrue();
        InitCommandTestFixture.IsIgnored(f.Root, Path.Combine(".twig", "config")).ShouldBeTrue();
        f.CredentialCalls.ShouldBe(1);
        var requests = f.Http.Requests;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(f.Root, ".twig", CoreWorkspaceFactory.FileName));
        f.Run("init", "--provider", "github", "--host", "github.com", "--repository", "original/repo",
            "--account", "fixture", "-o", "json").ShouldBe(1);
        using var error = JsonDocument.Parse(f.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("existing");
        f.Http.Requests.ShouldBe(requests);
        f.CredentialCalls.ShouldBe(1);
        (await File.ReadAllBytesAsync(Path.Combine(f.Root, ".twig", CoreWorkspaceFactory.FileName))).ShouldBe(bytes);
    }

    [Fact]
    public async Task FreshInitTelemetryFailureCannotChangeNativeResult()
    {
        using var f = new Fixture();
        f.Telemetry = Substitute.For<ITelemetryClient>();
        f.Telemetry.When(client => client.TrackEvent(Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, double>>()))
            .Do(_ => throw new InvalidOperationException("optional telemetry failure"));
        await f.Initialize(explicitStore: false);
        f.CredentialCalls.ShouldBe(1);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("number-reassigned", false)]
    [InlineData("number-reassigned", true)]
    [InlineData("node-number-moved", false)]
    [InlineData("node-number-moved", true)]
    public async Task CachedNumberMustBeRequalifiedBeforeExplicitRefreshOrMutation(string defect, bool mutation)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        var captured = await f.Snapshot();
        captured.Items.Single().Key.NativeId.ShouldBe("I_opaque");
        captured.Items.Single().Fields["github.number"].ShouldBe(FieldValue.Integer(42));
        captured.Proposals.ShouldBeEmpty();
        f.Http.Defect = defect;
        if (defect == "node-number-moved") f.Http.Issue["number"] = 43;
        var requests = f.Http.Requests;
        f.Run("show", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Http.Requests.ShouldBe(requests, "the old cached observation is not fresh native authority");
        using (var output = JsonDocument.Parse(f.Output)) output.RootElement.GetProperty("id").GetInt32().ShouldBe(42);
        f.Run(mutation
            ? ["update", "title", "must not write", "--id", "42", "-o", "json"]
            : ["show", "42", "--refresh", "-o", "json"]).ShouldBe(1);
        f.Http.Writes.ShouldBe(0);
        var after = await f.Snapshot();
        after.Version.ShouldBe(captured.Version, "mismatched native locator evidence must not even refresh the mirror");
        after.Items.Single().ShouldBe(captured.Items.Single());
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
    }

    [Fact]
    public async Task CachedNumberMustBeRequalifiedBeforeSavingAnExplicitDraft()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        await f.Stage(GitHubProvider.Title, FieldValue.String("must not save to old node"));
        var captured = await f.Snapshot();
        captured.Drafts.ShouldHaveSingleItem();
        f.Http.Defect = "number-reassigned";
        f.Run("save", "42", "--output", "json").ShouldBe(1);
        f.Http.Writes.ShouldBe(0);
        var after = await f.Snapshot();
        after.Version.ShouldBe(captured.Version);
        after.Drafts.Single().ShouldBe(captured.Drafts.Single());
        after.Proposals.ShouldBeEmpty();
    }

    [Fact]
    public async Task WorkspaceAliasUsesTheQualifiedOwnerAndVersionAndHelpAreOffline()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        f.Run("bench", "create", "Alias", "-o", "json").ShouldBe(0, f.Error);
        f.Run("bench", "switch", "Alias", "-o", "json").ShouldBe(0, f.Error);
        f.Run("workspace", "track", "42", "-o", "json").ShouldBe(0, f.Error);
        var workRequests = f.Http.WorkRequests;
        f.Run("workspace", "-o", "json").ShouldBe(0, f.Error);
        var canonical = f.Output;
        f.Run("ws", "-o", "json").ShouldBe(0, f.Error);
        f.Output.ShouldBe(canonical);
        f.Http.WorkRequests.ShouldBe(workRequests);
        var requests = f.Http.Requests;
        f.Run("version").ShouldBe(0, f.Error);
        f.Output.ShouldNotBeNullOrWhiteSpace();
        foreach (var args in new[] { new[] { "--help" }, new[] { "ws", "--help" }, new[] { "auth", "--help" } })
            f.Run(args).ShouldBe(0, $"{string.Join(" ", args)}\n{f.Error}\n{f.Output}");
        f.Http.Requests.ShouldBe(requests, "version/help must not acquire even metadata");
    }

    [Theory]
    [InlineData("new")]
    [InlineData("tree")]
    [InlineData("link")]
    public async Task UnqualifiedMachineFamilyRefusesWithOneJsonErrorBeforeAnyHttp(string family)
    {
        using var f = new Fixture();
        await f.Initialize();
        var requests = f.Http.Requests;
        var args = family switch
        {
            "new" => new[] { "new", "--type", GitHubProvider.Untyped, "--title", "quick", "--parent", "42", "-o", "json" },
            "tree" => ["tree", "42", "-o", "json"],
            _ => ["link", "related", "43", "--id", "42", "-o", "json"],
        };
        f.Run(args).ShouldBe(1);
        f.Output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(f.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain(family == "new" ? "retired" : "not qualified");
        f.Http.Requests.ShouldBe(requests);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public void FreshCoreInitRequiresExplicitGhSelectionBeforeAnyHttp()
    {
        using var f = new Fixture();
        f.Run("init", "--provider", "github", "--host", "github.com", "--repository", "original/repo",
            "--core-store", Path.Combine(f.Root, "new.db"), "-o", "json").ShouldBe(2);
        f.Error.ShouldContain("--account");
        f.Http.Requests.ShouldBe(0);
        f.CredentialCalls.ShouldBe(0);
        Directory.Exists(Path.Combine(f.Root, ".twig")).ShouldBeFalse();
    }

    [Fact]
    public async Task FreshNativeRootCreatesOriginalUuidAndNumericCommandsNeverUseNumberAsNodeIdentity()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("process", "description", GitHubProvider.Untyped, "-o", "json").ShouldBe(0, f.Error);
        f.Output.ShouldContain("github.number");
        f.Run("process", "GITHUB.ISSUE", "-o", "json").ShouldBe(1, "native type references are case-sensitive, not display-name aliases");
        f.Run("seed", "new", "--title", "Native seed", "--type", GitHubProvider.Untyped,
            "--no-parent", "--description", "**native**\n\nbody", "-o", "json").ShouldBe(0, f.Error);
        var seed = (await f.Snapshot()).Drafts.Single();
        seed.Key.IsLocal.ShouldBeTrue();
        seed.Changes[GitHubProvider.Assignees].ShouldBe(FieldValue.Strings(["U_actor"]));
        seed.Changes[GitHubProvider.StateField].Text.ShouldBe("OPEN");
        seed.Changes[GitHubProvider.Body].Kind.ShouldBe(FieldKind.Markdown);
        using var authored = JsonDocument.Parse(f.Output);
        var alias = authored.RootElement.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
        f.Run("seed", "publish", alias).ShouldBe(0, f.Error);
        var created = await f.Snapshot();
        var mapping = created.Mappings.Single();
        mapping.Local.ShouldBe(seed.Key);
        mapping.Remote.NativeId.ShouldBe("I_opaque");
        created.Journal.Single().Status.ShouldBe(OpStatus.Verified);
        created.Items.Single().Fields["github.number"].ShouldBe(FieldValue.Integer(42));
        f.Http.Creates.ShouldBe(1);
        var creationBytes = Core.Core.ProposalBytes(created.Proposals.Single());
        var requests = f.Http.Requests;
        var credentials = f.CredentialCalls;
        f.Run("show", "42", "-o", "json").ShouldBe(0, f.Error);
        using (var shown = JsonDocument.Parse(f.Output)) shown.RootElement.GetProperty("id").GetInt32().ShouldBe(42);
        f.Run("show-batch", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Run("show", alias, "-o", "json").ShouldBe(0, f.Error);
        f.Run("show", "--local-id", seed.Key.LocalId.ToString("D"), "-o", "json").ShouldBe(0, f.Error);
        f.Run("show", "99", "-o", "json").ShouldBe(1);
        f.Error.ShouldContain("--refresh");
        f.Http.Requests.ShouldBe(requests, "cached reads must do zero TOTAL HTTP, including viewer/schema/number lookup");
        f.CredentialCalls.ShouldBe(credentials);
        var cached = await f.Snapshot();
        cached.Version.ShouldBe(created.Version);
        cached.Mappings.Single().ShouldBe(mapping);
        Core.Core.ProposalBytes(cached.Proposals.Single()).ShouldBe(creationBytes);
        File.Exists(Path.Combine(f.Root, ".twig", "core-context.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task NativeMarkdownAndActualCloseUseVerifiedObservedNonAtomicEffects()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        f.Run("update", "description", "**unchanged Markdown**\n\nnext", "--id", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Http.Issue["body"]!.GetValue<string>().ShouldBe("**unchanged Markdown**\n\nnext");
        f.Run("note", "--text", "**note**\n\nnative", "--id", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Http.Comment.ShouldBe("**note**\n\nnative");
        f.Run("state", "CLOSED", "--id", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Http.Issue["state"]!.GetValue<string>().ShouldBe("CLOSED", "this must be OPEN -> CLOSED, not a no-op");
        var changed = await f.Snapshot();
        var transition = changed.Proposals.Single(p => p.Operations.Any(op => op.Id == "state"));
        transition.Operations.Single().Concurrency.ShouldBe(ConcurrencyMode.ObservedNonAtomic);
        changed.Journal.Single(row => row.Digest == transition.Digest).Status.ShouldBe(OpStatus.Verified);
        changed.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
    }

    [Fact]
    public async Task TypedPatchBenchAndBoundedQueryShareNativeNumbersAndRefuseUnqualifiedFamilies()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        f.Run("patch", "--id", "42", "--json", """{"github.title":"patched","github.assigneeIds":["U_actor"]}""",
            "-o", "json").ShouldBe(0, f.Error);
        await f.Stage(GitHubProvider.Body, FieldValue.RichText("**saved draft**", FieldKind.Markdown));
        (await f.Snapshot()).Drafts.ShouldHaveSingleItem();
        f.Run("save", "42", "--output", "json").ShouldBe(0, f.Error);
        f.Http.Issue["body"]!.GetValue<string>().ShouldBe("**saved draft**");
        (await f.Snapshot()).Drafts.ShouldBeEmpty();
        await f.Stage(GitHubProvider.Title, FieldValue.String("discarded edit"));
        var beforeDiscard = f.Http.Writes;
        f.Run("discard", "42", "--yes", "-o", "json").ShouldBe(0, f.Error);
        (await f.Snapshot()).Drafts.ShouldBeEmpty();
        f.Http.Writes.ShouldBe(beforeDiscard);
        f.Run("query", "--title", "patched", "-o", "json").ShouldBe(0, f.Error);
        f.Run("bench", "create", "Native", "-o", "json").ShouldBe(0, f.Error);
        f.Run("bench", "switch", "Native", "-o", "json").ShouldBe(0, f.Error);
        f.Run("workspace", "track", "42", "-o", "json").ShouldBe(0, f.Error);
        f.Run("workspace", "-o", "json").ShouldBe(0, f.Error);
        f.Output.ShouldContain("patched");
        var effects = f.Http.Writes;
        f.Run("batch", "--id", "42", "--state", "OPEN", "--note", "cannot couple", "-o", "json").ShouldBe(1);
        f.Http.Writes.ShouldBe(effects);
        f.Http.MoreCandidates = true;
        f.Run("query", "--title", "patched", "-o", "json").ShouldBe(1);
        f.Error.ShouldContain("100");
        var requests = f.Http.Requests;
        foreach (var args in new[] {
            new[] { "tree", "42", "-o", "json" }, new[] { "up" },
            new[] { "link", "related", "43", "--id", "42", "-o", "json" },
            new[] { "seed", "chain", "one;two", "--parent", "42", "--type", GitHubProvider.Untyped },
        }) f.Run(args).ShouldBe(1, f.Error);
        f.Http.Requests.ShouldBe(requests);
        var final = await f.Snapshot();
        final.Mappings.ShouldBeEmpty();
        final.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
    }

    [Fact]
    public async Task UnknownCreateSurvivesFreshRootReopenWithoutSecondCreateOrUuidReplacement()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("seed", "new", "--title", "Unknown", "--type", GitHubProvider.Untyped, "--no-parent", "-o", "json")
            .ShouldBe(0, f.Error);
        var original = (await f.Snapshot()).Drafts.Single();
        using var authored = JsonDocument.Parse(f.Output);
        var alias = authored.RootElement.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
        f.Http.LoseAcknowledgement = true;
        f.Run("seed", "publish", alias).ShouldBe(1);
        var fenced = await f.Snapshot();
        fenced.Drafts.Single().Key.ShouldBe(original.Key);
        fenced.Mappings.ShouldBeEmpty();
        fenced.Journal.Single().Status.ShouldBe(OpStatus.Indeterminate);
        f.Http.Creates.ShouldBe(1);
        f.Run("seed", "publish", alias).ShouldBe(1, "each invocation builds a new production DI root");
        f.Http.Creates.ShouldBe(1);
        var reopened = await f.Snapshot();
        reopened.Drafts.Single().Key.ShouldBe(original.Key);
        reopened.Journal.Single().Attempt.ShouldBe(fenced.Journal.Single().Attempt);
        reopened.Mappings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedNumberMetadataMustBePresentAndUniqueWithoutLookupFallback(bool duplicate)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        var stored = await f.Snapshot();
        var item = stored.Items.Single();
        var bad = duplicate
            ? new WorkItem(ItemKey.Remote(item.Key.Provider!, item.Key.ResourceScope!, "I_other"), item.Type, item.Fields, null)
            : new WorkItem(item.Key, item.Type, new(item.Fields.Where(field => field.Key != "github.number")), item.Revision);
        await f.ReplaceItems(duplicate ? [item, bad] : [bad]);
        var requests = f.Http.Requests;
        var version = (await f.Snapshot()).Version;
        f.Run("show", "42", "-o", "json").ShouldBe(1);
        f.Run("show-batch", "42", "-o", "json").ShouldBe(1);
        f.Http.Requests.ShouldBe(requests);
        (await f.Snapshot()).Version.ShouldBe(version);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("repository")]
    [InlineData("number")]
    public async Task NativeBoundaryMismatchRefusesBeforeAnyMutation(string defect)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Http.Defect = defect;
        f.Run("update", "title", "must not write", "--id", "42", "-o", "json").ShouldBe(1);
        f.Http.Writes.ShouldBe(0);
        (await f.Snapshot()).Proposals.ShouldBeEmpty();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CanonicalTempRoot.Create("twig-gh-spine-");
        internal NativeHttp Http { get; } = new();
        private readonly ConnectionBindingService _bindings;
        private readonly SqliteSystemWorktreeRegistry _registry;
        private readonly IBoundedProcessRunner _processes = Substitute.For<IBoundedProcessRunner>();
        private CoreWorkspaceDocument _document = null!;
        internal string Output = "", Error = "";
        internal int CredentialCalls;
        internal ITelemetryClient? Telemetry;
        private string Database => _document?.DatabasePath ?? Path.Combine(Root, "core.db");
        internal Fixture()
        {
            InitCommandTestFixture.InitTempWorktree(Root).ShouldBeTrue();
            Directory.Exists(Path.Combine(Root, ".twig")).ShouldBeFalse();
            _bindings = new(Path.Combine(Root, "home"));
            _registry = new(_bindings.RegistryPath, TimeProvider.System);
            _processes.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>()).Returns(call =>
            {
                CredentialCalls++;
                call.ArgAt<string>(0).ShouldBe("gh");
                call.ArgAt<IReadOnlyList<string>>(1).ShouldBe(["auth", "token", "--hostname", "github.com", "--user", "fixture"]);
                call.ArgAt<IReadOnlyList<string>>(5).ShouldContain("GH_TOKEN");
                return Task.FromResult(new BoundedProcessResult(BoundedProcessOutcome.Exited, 0, "synthetic-secret\n"));
            });
        }
        internal async Task Initialize(bool explicitStore = true)
        {
            var args = new[] { "init", "--provider", "github", "--host", "github.com", "--repository", "original/repo",
                "--account", "fixture", "-o", "json" }
                .Concat(explicitStore ? ["--core-store", Database] : Array.Empty<string>()).ToArray();
            Run(args).ShouldBe(0, Error);
            using var output = JsonDocument.Parse(Output);
            output.RootElement.GetProperty("coreInitialized").GetBoolean().ShouldBeTrue();
            _document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(Root, ".twig", "core-workspace.json")),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            _document.Connection.Provider.Value.ShouldBe("github");
            _document.PrincipalId.ShouldBe("U_actor");
            _document.Connection.Destination.ShouldBe(GitHubProvider.Destination("O_owner", "R_repo"));
            _document.DatabaseInstance.ShouldNotBe(Guid.Empty);
            _document.DatabasePath.ShouldBe(explicitStore ? Path.Combine(Root, "core.db") : Path.Combine(Root, ".twig", "core.db"));
            Directory.Exists(Path.Combine(Root, ".twig", "cache")).ShouldBeFalse();
            Http.WorkRequests.ShouldBe(0);
            Http.SchemaRequests.ShouldBe(0);
            Http.Writes.ShouldBe(0);
        }
        internal int Run(params string[] args)
        {
            var oldOut = Console.Out; var oldError = Console.Error;
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout); Console.SetError(stderr);
                return TwigConsoleApp.Run(args, services =>
                {
                    services.AddSingleton<IConnectionBindingService>(_bindings);
                    services.AddSingleton(_processes);
                    services.AddSingleton(new CoreTransportOverride(Http.NewHandle));
                    if (Telemetry is not null) services.AddSingleton(Telemetry);
                    services.AddTwigWorkspaceServices(Root, args, Path.Combine(Root, "display.json"));
                    services.AddSingleton(_registry);
                });
            }
            finally
            {
                Output = stdout.ToString(); Error = stderr.ToString(); Console.SetOut(oldOut); Console.SetError(oldError);
            }
        }
        internal async Task<StoreSnapshot> Snapshot()
        {
            var store = (await SqliteStore.OpenAsync(Database, expectedInstance: _document.DatabaseInstance)).Value;
            return (await store.ReadAsync(_document.Connection, default)).Value;
        }
        internal async Task ReplaceItems(IEnumerable<WorkItem> items, IEnumerable<Relationship>? relationships = null)
        {
            var store = (await SqliteStore.OpenAsync(Database, expectedInstance: _document.DatabaseInstance)).Value;
            var capture = await Snapshot();
            var saved = await store.CommitAsync(_document.Connection, capture.Version, new(capture.Version + 1, items,
                relationships ?? capture.Relationships, capture.Drafts, capture.Proposals, capture.Journal, capture.Mappings, capture.Benches), default);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        }
        internal async Task Stage(string field, FieldValue value)
            => await UseWorkspace(async workspace =>
            {
                var snapshot = await Snapshot();
                var staged = await workspace.StageEditAsync(snapshot.Items.Single().Key,
                    FieldValues.Empty.Set(field, value), default);
                staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            });

        internal async Task UseWorkspace(Func<CoreWorkspace, Task> action)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnectionBindingService>(_bindings);
            services.AddSingleton(_processes);
            services.AddSingleton(new CoreTransportOverride(Http.NewHandle));
            services.AddTwigWorkspaceServices(Root, ["pending", "-o", "json"], Path.Combine(Root, "display.json"));
            using var root = services.BuildServiceProvider();
            var opened = await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default);
            opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
            using var handle = opened.Value;
            await action(handle.Workspace);
        }
        public void Dispose()
        {
            _registry.Dispose(); _bindings.Dispose(); Http.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class NativeHttp : HttpMessageHandler
    {
        internal int Requests, WorkRequests, Writes, Creates, SchemaRequests;
        internal bool LoseAcknowledgement, MoreCandidates;
        internal string? Defect, Comment;
        internal JsonObject Issue { get; } = new()
        {
            ["__typename"] = "Issue", ["id"] = "I_opaque", ["number"] = 42, ["title"] = "original",
            ["body"] = "", ["state"] = "OPEN", ["updatedAt"] = "2026-10-31T12:00:00Z",
        };
        private JsonObject _assignees = Page([]);
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(NativeHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(request, ct);
        }
        private JsonObject Repository() => new() { ["id"] = Defect == "repository" ? "R_other" : "R_repo",
            ["owner"] = new JsonObject { ["id"] = "O_owner" } };
        private static JsonObject Names(params string[] names) => new() { ["fields"] = new JsonArray(names.Select(name =>
            (JsonNode)new JsonObject { ["name"] = name, ["type"] = name == "assigneeIds"
                ? new JsonObject { ["kind"] = "LIST", ["name"] = null, ["ofType"] = new JsonObject { ["kind"] = "SCALAR", ["name"] = "ID" } }
                : new JsonObject { ["kind"] = "SCALAR", ["name"] = name switch {
                    "id" or "repositoryId" => "ID", "number" => "Int", "state" => "IssueState",
                    "updatedAt" => "DateTime", "repository" => "Repository", "assignees" => "UserConnection", _ => "String" } } }).ToArray()) };
        private static JsonObject Page(IEnumerable<JsonNode> nodes, bool more = false) => new()
        {
            ["nodes"] = new JsonArray(nodes.ToArray()), ["pageInfo"] = new JsonObject { ["hasNextPage"] = more, ["endCursor"] = more ? "more" : null },
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            request.RequestUri!.AbsoluteUri.ShouldBe("https://api.github.com/graphql");
            request.Headers.Authorization!.Parameter.ShouldBe("synthetic-secret");
            var root = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var query = root["query"]!.GetValue<string>();
            if (query.Contains("__type(", StringComparison.Ordinal)) SchemaRequests++;
            var variables = root["variables"]!;
            if (!query.Contains("viewer", StringComparison.Ordinal) && !query.Contains("__type(", StringComparison.Ordinal)
                && !query.Contains("repository(owner:", StringComparison.Ordinal)) WorkRequests++;
            JsonNode data;
            if (query.StartsWith("mutation", StringComparison.Ordinal))
            {
                Writes++;
                var input = variables["input"]!;
                Guid.Parse(input["clientMutationId"]!.GetValue<string>()).ShouldNotBe(Guid.Empty);
                var mutation = query.Contains("createIssue(", StringComparison.Ordinal) ? "createIssue"
                    : query.Contains("addComment(", StringComparison.Ordinal) ? "addComment" : "updateIssue";
                if (mutation == "createIssue") { Creates++; input["repositoryId"]!.GetValue<string>().ShouldBe("R_repo"); }
                else (input[mutation == "addComment" ? "subjectId" : "id"]!.GetValue<string>()).ShouldBe("I_opaque");
                foreach (var field in new[] { "title", "body", "state" })
                    if (mutation != "addComment" && input[field] is { } value) Issue[field] = value.DeepClone();
                if (input["assigneeIds"] is JsonArray assignees)
                    _assignees = Page(assignees.Select(id => (JsonNode)new JsonObject { ["id"] = id!.GetValue<string>() }));
                Issue["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var payload = new JsonObject { ["clientMutationId"] = input["clientMutationId"]!.DeepClone() };
                if (mutation == "addComment")
                {
                    Comment = input["body"]!.GetValue<string>();
                    payload["commentEdge"] = new JsonObject { ["node"] = new JsonObject { ["id"] = "C_native" } };
                }
                else payload["issue"] = new JsonObject { ["id"] = "I_opaque", ["repository"] = Repository() };
                if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new IOException("lost native acknowledgement after creation"); }
                data = new JsonObject { [mutation] = payload };
            }
            else if (query.Contains("repository(owner:", StringComparison.Ordinal))
            {
                var repo = Repository(); repo["nameWithOwner"] = "original/repo"; data = new JsonObject { ["repository"] = repo };
            }
            else if (query.Contains("viewer", StringComparison.Ordinal))
                data = new JsonObject { ["viewer"] = new JsonObject { ["id"] = Defect == "actor" ? "U_other" : "U_actor", ["login"] = "fixture" },
                    ["node"] = Repository() };
            else if (query.Contains("__type(", StringComparison.Ordinal))
            {
                var update = Names("id", "title", "body", "state", "assigneeIds"); update["inputFields"] = update["fields"]!.DeepClone(); update.Remove("fields");
                var create = Names("repositoryId", "title", "body", "assigneeIds"); create["inputFields"] = create["fields"]!.DeepClone(); create.Remove("fields");
                data = new JsonObject { ["issue"] = Names("id", "number", "title", "body", "state", "updatedAt", "repository", "assignees"),
                    ["issueType"] = null, ["repository"] = Names("issues"),
                    ["states"] = new JsonObject { ["enumValues"] = new JsonArray(new JsonObject { ["name"] = "OPEN" }, new JsonObject { ["name"] = "CLOSED" }) },
                    ["mutations"] = Names("createIssue", "updateIssue", "addComment"), ["update"] = update, ["create"] = create };
            }
            else if (query.Contains("issue(number:", StringComparison.Ordinal))
            {
                variables["id"]!.GetValue<string>().ShouldBe("R_repo");
                variables["number"]!.GetValue<int>().ShouldBe(42);
                var repo = Repository(); var issue = (JsonObject)Issue.DeepClone(); issue["repository"] = Repository();
                repo["__typename"] = "Repository";
                if (Defect == "number-reassigned") issue["id"] = "I_rebound";
                if (Defect == "number") issue["number"] = 43;
                repo["issue"] = issue; data = new JsonObject { ["node"] = repo };
            }
            else if (query.Contains("issues(first:", StringComparison.Ordinal))
            {
                var repo = Repository(); repo["issues"] = Page([new JsonObject { ["id"] = "I_opaque" }], MoreCandidates);
                data = new JsonObject { ["node"] = repo };
            }
            else if (query.Contains("... on Label", StringComparison.Ordinal))
                data = new JsonObject { ["node"] = new JsonObject { ["id"] = "U_actor", ["__typename"] = "User" } };
            else if (query.Contains("assignees(first:", StringComparison.Ordinal))
                data = new JsonObject { ["node"] = new JsonObject { ["id"] = "I_opaque", ["__typename"] = "Issue", ["assignees"] = _assignees.DeepClone() } };
            else if (query.Contains("... on IssueComment", StringComparison.Ordinal))
                data = new JsonObject { ["node"] = new JsonObject { ["__typename"] = "IssueComment", ["id"] = "C_native", ["body"] = Comment,
                    ["author"] = new JsonObject { ["id"] = "U_actor" }, ["issue"] = new JsonObject { ["id"] = "I_opaque", ["repository"] = Repository() } } };
            else
            {
                variables["id"]!.GetValue<string>().ShouldBe("I_opaque");
                var issue = (JsonObject)Issue.DeepClone(); issue["repository"] = Repository();
                data = new JsonObject { ["node"] = issue };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
