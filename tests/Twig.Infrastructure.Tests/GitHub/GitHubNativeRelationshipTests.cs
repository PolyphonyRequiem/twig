using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubNativeRelationshipTests
{
    [Theory]
    [InlineData("parent", true)]
    [InlineData("parent", false)]
    [InlineData("predecessor", true)]
    [InlineData("predecessor", false)]
    [InlineData("successor", true)]
    [InlineData("successor", false)]
    public async Task ProductionComposition_DigestConfirmsJournalsWritesAndReadsBackBothEndpoints(string relation, bool add)
    {
        using var fixture = await Fixture.CreateAsync(relation, add);
        var (path, digest) = await fixture.PreviewAsync();

        var result = await fixture.ApplyAsync(path, digest);

        result.Error.ShouldBeNull();
        result.Failed.ShouldBeFalse();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        fixture.Network.Edge.ShouldBe(add);
        var journal = (await fixture.Journal.GetAsync(digest))!;
        var authority = journal.Origin.ShouldNotBeNull().ProviderAuthority.ShouldNotBeNull();
        authority.ActorId.ShouldBe("U_actor");
        authority.Target.ShouldBe(GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql")));
        fixture.Network.WriterCredential.ShouldBe(fixture.Network.LastViewerCredentialBeforeWrite);
        fixture.Network.ReadIdsAfterWrite.ShouldContain("I_item");
        fixture.Network.ReadIdsAfterWrite.ShouldContain("I_peer");
        fixture.Network.WrittenQuery.ShouldBe(GitHubRelationshipMutations.Create(fixture.Operation!, "https://api.github.com").Query);
        (await fixture.ApplyAsync(path, digest)).Failed.ShouldBeFalse();
        fixture.Network.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("actor")]
    [InlineData("attachment")]
    [InlineData("peer-repository")]
    [InlineData("mapping")]
    public async Task ChangedPrecondition_RefusesWithoutNativeWriteOrLocalFallback(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        switch (change)
        {
            case "token": fixture.Network.Title = "Changed"; break;
            case "actor": fixture.Network.Actor = "U_changed"; break;
            case "attachment":
                var attachment = (await fixture.Target.Attachment.ReadWithRevisionAsync()).Value;
                (await fixture.Target.Attachment.ClearAsync(attachment.Envelope!.BindingId!.Value, attachment.Revision))
                    .IsSuccess.ShouldBeTrue();
                break;
            case "peer-repository": fixture.Network.PeerRepository = "R_other"; break;
            case "mapping":
                await fixture.Local.DeleteAsync(fixture.Operation!.Other!.LocalId, 1);
                break;
        }

        var result = await fixture.ApplyAsync(path, digest);

        result.Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
        string.Join("; ", result.Operations.Select(o => o.Error).Append(result.Error))
            .ShouldNotContain("fixture-credential");
    }

    [Fact]
    public async Task ExactRevisionDefault_IsReviewableButNeverImportedOrApplied()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync("exact-revision");
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeFalse();
        preview.Issues.ShouldContain(i => i.Code == PlanValidationCodes.UnsupportedApplyVersion);
        (await fixture.Journal.GetAsync(preview.Digest!)).ShouldBeNull();
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task DigestMismatch_RemainsUnappliedWithoutNativeWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        var mismatchedDigest = (digest[0] == '0' ? "1" : "0") + digest[1..];
        var result = await fixture.Lifecycle.ApplyAsync(path, mismatchedDigest);
        result.Failed.ShouldBeTrue();
        (result.Error ?? throw new InvalidOperationException("Expected a digest-mismatch refusal."))
            .ShouldContain("does not match confirmed digest");
        (await fixture.Journal.GetAsync(digest))!.Operations.Single().State.ShouldBe(PlanOperationState.Planned);
        fixture.Network.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("lost-response", true, true)]
    [InlineData("lost-response", false, false)]
    [InlineData("contradiction", false, false)]
    [InlineData("graphql-error", true, true)]
    [InlineData("redirect", false, false)]
    public async Task UncertainOrContradictoryResponse_UsesNativeReadbackAndNeverReplays(
        string response, bool effect, bool verified)
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        fixture.Network.Response = response;
        fixture.Network.ApplyEffect = effect;

        var first = await fixture.ApplyAsync(path, digest);
        first.Failed.ShouldBe(!verified);
        first.Operations.Single().State.ShouldBe(verified ? PlanOperationState.Verified : PlanOperationState.Failed);
        await fixture.ApplyAsync(path, digest);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DeniedReadback_RemainsIndeterminateWithoutReplay()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        fixture.Network.DenyReadback = true;
        var result = await fixture.ApplyAsync(path, digest);
        result.Failed.ShouldBeTrue();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        await fixture.ApplyAsync(path, digest);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task CrashRecovery_ObservesNativeEffectWithoutIssuingMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.Journal.ConfirmAsync(digest, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "link",
            PlanOperationState.Planned, PlanOperationState.Confirmed, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "link",
            PlanOperationState.Confirmed, PlanOperationState.Applying, old);
        fixture.Network.Edge = true;

        var result = await fixture.ApplyAsync(path, digest);

        result.Error.ShouldBeNull();
        result.Failed.ShouldBeFalse();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task JournalRejectsConcurrencyDowngradeAgainstConfirmedArtifact()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync();
        var parsed = new PlanDocumentParser().Parse(await File.ReadAllTextAsync(path));
        var altered = parsed.Plan! with
        {
            Operations = [fixture.Operation! with { Concurrency = "exact-revision" }],
        };
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            fixture.Journal.ImportAsync(altered, parsed.CanonicalJson!, parsed.Digest!, path, DateTimeOffset.UtcNow));
        error.Message.ShouldContain("concurrency");
        (await fixture.Journal.GetAsync(parsed.Digest!)).ShouldBeNull();
    }

    [Fact]
    public void ObservationToken_IsOrderIndependentButChangesWithFieldsAndRelationships()
    {
        var item = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_item");
        var peer = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_peer");
        var third = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_third");
        var fields = FieldValues.Empty.Set("title", new TextValue("Title"));
        ProviderWorkItemRelationship[] edges =
        [
            new(item, peer, LinkKind.ParentChild),
            new(third, item, LinkKind.PredecessorSuccessor),
        ];
        var token = GitHubObservationToken.Create(item, fields, edges);
        GitHubObservationToken.Create(item, fields, edges.Reverse()).ShouldBe(token);
        GitHubObservationToken.Create(item, fields.Set("title", new TextValue("Changed")), edges).ShouldNotBe(token);
        GitHubObservationToken.Create(item, fields, []).ShouldNotBe(token);
    }

    private sealed class Fixture : IDisposable
    {
        internal GitHubTargetFixture Target { get; } = new();
        private readonly SqliteCacheStore _store = new("Data Source=:memory:");
        internal Network Network { get; } = new();
        private ServiceProvider _services = null!;
        internal SqliteLocalWorkItemRepository Local { get; private set; } = null!;
        internal IPlanJournalRepository Journal => _services.GetRequiredService<IPlanJournalRepository>();
        internal IPlanLifecycleService Lifecycle => _services.GetRequiredService<IPlanLifecycleService>();
        internal ProviderMutationOperation? Operation { get; private set; }
        private readonly ExternalWorkItemIdentity _item = new("github", "https://api.github.com", "I_item");
        private readonly ExternalWorkItemIdentity _peer = new("github", "https://api.github.com", "I_peer");

        internal static async Task<Fixture> CreateAsync(string relation = "parent", bool add = true)
        {
            var fixture = new Fixture();
            await fixture.Target.BindAsync();
            fixture.Network.Relation = relation;
            fixture.Network.Add = add;
            fixture.Network.Edge = !add;
            var config = new TwigConfiguration
            {
                Provider = "github", GitHub = new() { Host = "github.com", Repository = "owner/repo" },
                GitHubAccount = "selected-account",
            };
            var services = new ServiceCollection();
            services.AddTwigInfrastructure(config, fixture.Target.Paths.TwigDir, fixture.Target.Root);
            services.AddConnectionDomainServices();
            fixture.Target.Register(services);
            services.AddSingleton(fixture._store);
            services.AddSingleton<IGitHubTokenSource>(new Tokens());
            services.AddSingleton(new GitHubTransportOverride(() => new Forwarder(fixture.Network)));
            fixture._services = services.BuildServiceProvider();
            fixture.Local = fixture._services.GetRequiredService<SqliteLocalWorkItemRepository>();
            return fixture;
        }

        internal async Task<string> WriteAsync(string concurrency = "observed-non-atomic")
        {
            var snapshot = await _services.GetRequiredService<IWorkItemSnapshotReader>().FetchSnapshotAsync(_item);
            var item = await Local.ResolveAsync(_item, snapshot.RevisionToken);
            var peer = await Local.ResolveAsync(_peer, null);
            Operation = new ProviderMutationOperation
            {
                Id = "link", OperationKind = Network.Add ? PlanOperationKind.AddLink : PlanOperationKind.RemoveLink,
                Concurrency = concurrency, Relation = Network.Relation,
                Target = new() { Item = new() { LocalId = item.Id, ExternalIdentity = _item },
                    ExpectedProviderRevision = snapshot.RevisionToken! },
                Other = new() { LocalId = peer.Id, ExternalIdentity = _peer },
            };
            var path = Path.Combine(Target.Root, "proposal.json");
            await File.WriteAllTextAsync(path, PlanDocumentWriter.Write(new()
            {
                Version = 2, Workspace = new() { Organization = "github.com", Project = "owner/repo" },
                Operations = [Operation],
            }));
            return path;
        }

        internal async Task<(string Path, string Digest)> PreviewAsync()
        {
            var path = await WriteAsync();
            var preview = await Lifecycle.PreviewAsync(path);
            preview.CanApply.ShouldBeTrue(string.Join("; ", preview.Issues.Select(i => i.Message)));
            preview.ReviewModel.ShouldNotBeNull().Operations.Single().Summary.ShouldContain("non-atomic");
            return (path, preview.Digest!);
        }

        internal Task<PlanApplyResult> ApplyAsync(string path, string digest) =>
            Lifecycle.ApplyAsync(path, digest);

        public void Dispose()
        {
            _services?.Dispose();
            _store.Dispose();
            Target.Dispose();
        }
    }

    private sealed class Tokens : IGitHubTokenSource
    {
        private int _calls;
        public ValueTask<string> GetTokenAsync(CancellationToken ct) =>
            ValueTask.FromResult("fixture-credential-" + ++_calls);
    }

    private sealed class Forwarder(Network network) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            network.SendAsync(request, ct);
    }

    private sealed class Network
    {
        internal string Relation { get; set; } = "parent";
        internal bool Add { get; set; } = true;
        internal bool Edge { get; set; }
        internal bool ApplyEffect { get; set; } = true;
        internal bool DenyReadback { get; set; }
        internal string Response { get; set; } = "ack";
        internal string Actor { get; set; } = "U_actor";
        internal string Title { get; set; } = "Title";
        internal string PeerRepository { get; set; } = "R_repo";
        internal int Writes { get; private set; }
        internal string? WrittenQuery { get; private set; }
        internal string? WriterCredential { get; private set; }
        internal string? LastViewerCredentialBeforeWrite { get; private set; }
        internal HashSet<string> ReadIdsAfterWrite { get; } = [];

        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri.ShouldBe(new("https://api.github.com/graphql"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = document.RootElement.GetProperty("query").GetString()!;
            var vars = document.RootElement.GetProperty("variables");
            var credential = request.Headers.Authorization!.Parameter;
            string body;
            if (query == GitHubIssueQueries.AuthenticatedViewer)
            {
                if (Writes == 0) LastViewerCredentialBeforeWrite = credential;
                body = $$$$$$"""{"data":{"viewer":{"id":"{{{{{{Actor}}}}}}","login":"locator"}}}""";
            }
            else if (query.StartsWith("mutation", StringComparison.Ordinal))
            {
                Writes++;
                WrittenQuery = query;
                WriterCredential = credential;
                var issueId = vars.GetProperty("issueId").GetString();
                issueId.ShouldBe(Relation is "parent" or "successor" ? "I_peer" : "I_item");
                vars.GetProperty(Relation == "parent" ? "subIssueId" : "blockingIssueId").GetString()
                    .ShouldBe(Relation is "parent" or "successor" ? "I_item" : "I_peer");
                if (ApplyEffect) Edge = Add;
                if (Response == "lost-response") throw new HttpRequestException("private provider response");
                if (Response == "redirect") return Json("{}", HttpStatusCode.TemporaryRedirect);
                if (Response == "graphql-error") return Json("""{"errors":[{"message":"private response"}]}""");
                var name = (Add ? "add" : "remove") + (Relation == "parent" ? "SubIssue" : "BlockedBy");
                body = $$$$$$"""{"data":{"{{{{{{name}}}}}}":{"clientMutationId":null}}}""";
            }
            else
            {
                var id = vars.GetProperty("id").GetString()!;
                if (Writes > 0) ReadIdsAfterWrite.Add(id);
                if (Writes > 0 && DenyReadback) return Json("{}", HttpStatusCode.Forbidden);
                var other = id == "I_item" ? "I_peer" : "I_item";
                if (query == GitHubIssueQueries.Snapshot)
                {
                    var parent = Edge && Relation == "parent" && id == "I_item"
                        ? """{"id":"I_peer"}""" : "null";
                    var repository = id == "I_peer" ? PeerRepository : "R_repo";
                    body = $$$$$$"""
                        {"data":{"node":{"__typename":"Issue","id":"{{{{{{id}}}}}}","number":1,"title":"{{{{{{Title}}}}}}","state":"OPEN",
                        "body":"","url":"https://example.test/issue","issueType":null,"parent":{{{{{{parent}}}}}},"milestone":null,
                        "repository":{"id":"{{{{{{repository}}}}}}","nameWithOwner":"owner/repo","owner":{"id":"O_owner"}}}}}
                        """;
                }
                else
                {
                    var field = new[] { "labels", "assignees", "subIssues", "blockedBy", "blocking" }
                        .Single(f => query == GitHubIssueQueries.Connection(f));
                    var hasEdge = Edge && (Relation, id, field) is
                        ("parent", "I_peer", "subIssues") or
                        ("predecessor", "I_item", "blockedBy") or ("predecessor", "I_peer", "blocking") or
                        ("successor", "I_peer", "blockedBy") or ("successor", "I_item", "blocking");
                    var nodes = hasEdge ? $$"""[{"id":"{{other}}"}]""" : "[]";
                    body = $$$$$$"""
                        {"data":{"node":{"__typename":"Issue","id":"{{{{{{id}}}}}}","{{{{{{field}}}}}}":{
                        "nodes":{{{{{{nodes}}}}}},"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                        """;
                }
            }
            return Json(body);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
