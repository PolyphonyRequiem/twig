using System.Text.Json;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class CleanCoreGitHubAccessTests
{
    [Fact]
    public async Task ProductionCoreHostUsesNativeLeaseAndHttpWithOriginalJournalAttempt()
    {
        using var native = new GitHubTargetFixture();
        await native.BindAsync();
        var config = new TwigConfiguration { Provider = "github", GitHub = new() { Host = "github.com", Repository = "original/repo" } };
        await config.SaveAsync(native.Paths.ConfigPath);
        GitHubWorkspaceScope.TryCreate(config.GitHub, out var scope, out _).ShouldBeTrue();
        var targets = new GitHubBoundTargetResolver(native.Attachment, native.Registry, scope!);
        var origin = await targets.ResolveAsync(default);
        var connection = new Connection(new("github"), "https://api.github.com", GitHubProvider.Destination("O_owner", "R_repo"),
            CleanCoreGitHubAccess.Identity(origin, config, "U_actor"));
        var database = Path.Combine(native.Root, "host-core.db");
        var store = (await SqliteStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(native.Paths, store, database, connection, "U_actor", default)).IsSuccess.ShouldBeTrue();
        var token = Substitute.For<IGitHubTokenSource>();
        token.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult("synthetic-secret"));
        var bindings = Substitute.For<IConnectionBindingService>();
        bindings.RegistryPath.Returns(native.SystemDbPath);
        using var network = new NativeHttp(native.Paths.TwigDir, store, connection);
        var services = new ServiceCollection();
        services.AddSingleton(bindings);
        services.AddSingleton(token);
        services.AddSingleton(new CoreTransportOverride(() => network.NewHandle()));
        services.AddCoreWorkspaceServices(config, native.Paths);
        using var root = services.BuildServiceProvider();
        var factory = root.GetRequiredService<CoreWorkspaceFactory>();
        var opened = await factory.OpenAsync(default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var runtime = opened.Value;
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "I_issue");
        var staged = await runtime.Workspace.StageEditAsync(key, new([KeyValuePair.Create(GitHubProvider.Title, FieldValue.String("host edit"))]), default);
        staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        var preview = await runtime.Workspace.PreviewDraftAsync(key, default);
        preview.IsSuccess.ShouldBeTrue(preview.IsSuccess ? "" : preview.Error.Message);
        var saved = await runtime.Workspace.SaveDraftAsync(preview.Value, preview.Value.Proposal!.Digest, default);
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.EffectsVerified.ShouldBeTrue(saved.Value.Error?.Message);
        network.Writes.ShouldBe(1);
        var complete = (await store.ReadAsync(connection, default)).Value;
        complete.Drafts.ShouldBeEmpty();
        complete.Journal.Single().Status.ShouldBe(OpStatus.Verified);
        complete.Items.Single().Fields[GitHubProvider.Title].Text.ShouldBe("host edit");
        var row = complete.Journal.Single();
        var intentFile = Path.Combine(native.Paths.TwigDir, "core-github-intents", row.Attempt.ToString("D") + ".json");
        File.Exists(intentFile + ".ack").ShouldBeTrue();
        (await runtime.Workspace.ApplyAsync(preview.Value.Proposal, preview.Value.Proposal.Digest, default)).Value.EffectsVerified.ShouldBeTrue();
        network.Writes.ShouldBe(1, "the native host must not dispatch an already verified proposal twice");
        network.Actor = "U_other";
        var wrongActor = await runtime.Workspace.ReadAsync(key, default);
        wrongActor.IsSuccess.ShouldBeFalse();
        network.Writes.ShouldBe(1);
        typeof(CoreWorkspaceFactory).Assembly.GetReferencedAssemblies()
            .ShouldNotContain(reference => reference.Name == "Twig.Domain");
    }

    [Fact]
    public async Task NativeTargetLeaseAndDurableAttemptAreRequiredAndAcknowledgementIsAttributed()
    {
        using var native = new GitHubTargetFixture();
        await native.BindAsync();
        var config = new TwigConfiguration { Provider = "github", GitHub = new() { Host = "github.com", Repository = "original/repo" } };
        await config.SaveAsync(native.Paths.ConfigPath);
        GitHubWorkspaceScope.TryCreate(config.GitHub, out var scope, out _).ShouldBeTrue();
        var targets = new GitHubBoundTargetResolver(native.Attachment, native.Registry, scope!);
        var origin = await targets.ResolveAsync(default);
        var token = Substitute.For<IGitHubTokenSource>();
        token.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult("synthetic-secret"));
        var connection = new Connection(new("github"), "https://api.github.com", GitHubProvider.Destination("O_owner", "R_repo"),
            CleanCoreGitHubAccess.Identity(origin, config, "U_actor"));
        var store = (await SqliteStore.CreateAsync(Path.Combine(native.Root, "core.db"))).Value;
        (await store.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        var access = new CleanCoreGitHubAccess(config, native.Paths, scope!, targets, origin, token, connection, "U_actor", store);
        var acquired = await access.AcquireAsync(connection, default);
        acquired.IsSuccess.ShouldBeTrue(acquired.IsSuccess ? "" : acquired.Error.Message);
        using var session = acquired.Value;
        var lockPath = Path.Combine(native.Paths.TwigDir, ProviderTargetAttachmentDocument.LockFileName);
        Should.Throw<IOException>(() => new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "I_issue");
        var op = new Op("edit", OpKind.Batch, key, new(key, "observed-native"),
            new([KeyValuePair.Create(GitHubProvider.Title, FieldValue.String("new title"))]), concurrency: ConcurrencyMode.ObservedNonAtomic);
        var proposal = new Proposal(connection, "schema", 0, [op]);
        var attempt = Guid.NewGuid();
        var payload = """{"query":"mutation($input: UpdateIssueInput!) { updateIssue(input: $input) { clientMutationId issue { id repository { id owner { id } } } } }","variables":{"input":{"clientMutationId":"ATTEMPT","id":"I_issue","title":"new title"}}}"""
            .Replace("ATTEMPT", attempt.ToString("D"), StringComparison.Ordinal);
        var intent = new GitHubWriteIntent(proposal, 0, attempt, scope!.GraphQlEndpoint, "POST", "application/json", payload);
        (await session.AdmitAsync(intent, default)).IsSuccess.ShouldBeFalse("a token is not Applying authority");
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        var imported = await store.CommitAsync(connection, 0, new(1, proposals: [proposal], journal: [planned]), default);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var row = planned.Begin(attempt).Value;
        var begun = await store.CommitAsync(connection, 1, new(2, proposals: [proposal], journal: [row]), default);
        begun.IsSuccess.ShouldBeTrue(begun.IsSuccess ? "" : begun.Error.Message);
        (await session.AdmitAsync(intent with { Attempt = Guid.NewGuid() }, default)).IsSuccess.ShouldBeFalse();
        (await session.AdmitAsync(intent with { Payload = payload.Replace("new title", "unreviewed title", StringComparison.Ordinal) }, default)).IsSuccess.ShouldBeFalse();
        (await session.AdmitAsync(intent with { Payload = " " + payload }, default)).IsSuccess.ShouldBeFalse("equivalent but noncanonical bytes are not retained intent");
        var policyPath = Path.Combine(native.Root, "twig.policy.json");
        await File.WriteAllTextAsync(policyPath, """{"version":1,"rules":[]}""");
        (await session.AdmitAsync(intent, default)).IsSuccess.ShouldBeFalse("actual policy bytes changed after the retained proposal");
        File.Delete(policyPath);
        var admitted = await session.AdmitAsync(intent, default);
        admitted.IsSuccess.ShouldBeTrue(admitted.IsSuccess ? "" : admitted.Error.Message);
        using var admission = admitted.Value;
        var path = Path.Combine(native.Paths.TwigDir, "core-github-intents", attempt.ToString("D") + ".json");
        var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), CoreGitHubNativeJsonContext.Default.CoreGitHubNativeIntent)!;
        record.StoreInstance.ShouldBe(store.InstanceId);
        record.Actor.ShouldBe("U_actor");
        record.Digest.ShouldBe(proposal.Digest);
        record.Attempt.ShouldBe(attempt);
        record.Payload.ShouldBe(payload);
        (await session.AdmitAsync(intent, default)).IsSuccess.ShouldBeFalse("the original durable native intent cannot be replaced or admitted twice");
        await admission.ObserveAsync(200, """{"data":"synthetic-secret"}""", default);
        var acknowledgement = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path + ".ack"),
            CoreGitHubNativeJsonContext.Default.CoreGitHubNativeAcknowledgement)!;
        acknowledgement.RequestHash.ShouldBe(record.RequestHash);
        acknowledgement.Body.ShouldNotContain("synthetic-secret");
        await Should.ThrowAsync<IOException>(() => admission.ObserveAsync(200, "replacement", default));
        var schema = new ProcessSchema(connection, [], []);
        var projection = await session.ReadRuleProjectionAsync(schema, default);
        projection.IsSuccess.ShouldBeTrue();
        projection.Value.Scope.ShouldBe(GitHubValidationScope.NativeApiAndDeclaredTwigPolicies);
        projection.Value.ActorId.ShouldBe("U_actor");
        projection.Value.NativeFingerprint.ShouldBe(schema.Fingerprint);
        session.Dispose();
        await Should.ThrowAsync<InvalidOperationException>(() => session.ValidateAsync(default));
        using var released = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class NativeHttp(string twig, SqliteStore store, Connection connection) : HttpMessageHandler
    {
        internal string Actor { get; set; } = "U_actor";
        internal int Writes { get; private set; }
        private string _title = "native issue";
        private string _updated = "2026-10-31T12:00:00Z";
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(NativeHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(request, ct);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri.ShouldBe(new Uri("https://api.github.com/graphql"));
            request.Headers.Authorization!.Parameter.ShouldBe("synthetic-secret");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = body.RootElement.GetProperty("query").GetString()!;
            var repo = new { id = "R_repo", owner = new { id = "O_owner" } };
            if (query.StartsWith("mutation", StringComparison.Ordinal))
            {
                var input = body.RootElement.GetProperty("variables").GetProperty("input");
                var attempt = Guid.Parse(input.GetProperty("clientMutationId").GetString()!);
                var snapshot = (await store.ReadAsync(connection, ct)).Value;
                snapshot.Journal.ShouldContain(row => row.Status == OpStatus.Applying && row.Attempt == attempt);
                File.Exists(Path.Combine(twig, "core-github-intents", attempt.ToString("D") + ".json")).ShouldBeTrue();
                Should.Throw<IOException>(() => new FileStream(Path.Combine(twig, ProviderTargetAttachmentDocument.LockFileName),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                Writes++;
                _title = input.GetProperty("title").GetString()!;
                _updated = "2026-10-31T12:00:01Z";
                return Reply(new { updateIssue = new { clientMutationId = attempt.ToString("D"), issue = new { id = "I_issue", repository = repo } } });
            }
            if (query.Contains("__type(", StringComparison.Ordinal))
            {
                object Field(string name, string type) => new { name, type = new { kind = "SCALAR", name = type } };
                return Reply(new
                {
                    issue = new { fields = new[] { Field("id", "ID"), Field("number", "Int"), Field("title", "String"), Field("body", "String"),
                        Field("state", "IssueState"), Field("updatedAt", "DateTime"), Field("repository", "Repository") } },
                    issueType = (object?)null,
                    repository = new { fields = new[] { new { name = "issues" } } },
                    states = new { enumValues = new[] { new { name = "OPEN" }, new { name = "CLOSED" } } },
                    mutations = new { fields = new[] { new { name = "updateIssue" } } },
                    update = new { inputFields = new[] { Field("id", "ID"), Field("title", "String"), Field("clientMutationId", "String") } },
                    create = new { inputFields = Array.Empty<object>() },
                });
            }
            if (query.Contains("viewer", StringComparison.Ordinal))
                return Reply(new { viewer = new { id = Actor }, node = new { __typename = "Repository", id = "R_repo", owner = new { id = "O_owner" } } });
            return Reply(new { node = new { __typename = "Issue", id = "I_issue", number = 42, title = _title, body = "", state = "OPEN", updatedAt = _updated, repository = repo } });
        }
        private static HttpResponseMessage Reply(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
    }
}
