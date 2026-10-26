using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubCapturedReadSessionTests
{
    [Theory]
    [InlineData("github.com", "https://api.github.com/graphql")]
    [InlineData("ghe.example", "https://ghe.example/api/graphql")]
    public async Task RealComposition_CapturesRotatingCredentialOnce_ForViewerAndEverySnapshotRead(
        string host, string endpoint)
    {
        using var fixture = new Fixture(host, endpoint);
        var factory = fixture.Services.GetRequiredService<GitHubCapturedReadSessionFactory>();
        fixture.Tokens.Calls.ShouldBe(0);
        fixture.Network.Requests.ShouldBeEmpty();
        await fixture.Attachment.DidNotReceive().ReadWithRevisionAsync(Arg.Any<CancellationToken>());

        using var session = await factory.CaptureAsync();
        var snapshot = await session.FetchSnapshotAsync(fixture.Issue);
        await session.FetchSnapshotAsync(fixture.Issue);

        session.Actor.ActorId.ShouldBe("U_actor");
        session.Actor.Login.ShouldBe("observed-login");
        session.Actor.ResourceScope.ShouldBe(fixture.Issue.ResourceScope);
        session.SelectedAccount.ShouldBe("explicit-account");
        session.GraphQlEndpoint.ShouldBe(new Uri(endpoint));
        session.Target.BindingId.ShouldBe(fixture.Binding.Id);
        session.Target.Identity.ShouldBe(fixture.Binding.Identity);
        session.Target.BindingRevision.ShouldBe(7);
        session.Target.AttachmentRevision.ShouldBe(11);
        session.Target.WorktreeFingerprint.ShouldBe("observed-worktree-fingerprint");
        snapshot.Identity.ShouldBe(fixture.Issue);
        snapshot.RevisionToken.ShouldBeNull();
        snapshot.RelationshipsComplete.ShouldBeTrue();
        fixture.Tokens.Calls.ShouldBe(1);
        fixture.Network.Requests.Count.ShouldBe(13);
        fixture.Network.Requests.ShouldAllBe(r =>
            r.Authorization == "Bearer fixture-credential-1" && r.Endpoint == new Uri(endpoint));
        fixture.Network.Requests[0].Query.ShouldBe(GitHubIssueQueries.AuthenticatedViewer);
        fixture.Network.Requests.Skip(1).ShouldAllBe(r =>
            r.Query != GitHubIssueQueries.AuthenticatedViewer && IsSnapshotQuery(r.Query));
        session.ToString()!.ShouldNotContain("fixture-credential");
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("binding-revision")]
    [InlineData("attachment-revision")]
    [InlineData("binding-id")]
    [InlineData("identity")]
    [InlineData("detached")]
    public async Task SelectionDriftDuringViewer_RefusesSessionAndDisposesTransport(string change)
    {
        using var fixture = new Fixture();
        fixture.Network.AfterViewer = () => fixture.ChangeSelection(change);

        var error = await Should.ThrowAsync<InvalidDataException>(() => fixture.CaptureAsync());

        error.ToString().ShouldNotContain("fixture-credential");
        fixture.Tokens.Calls.ShouldBe(1);
        fixture.Network.Requests.Count.ShouldBe(1);
        fixture.Network.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("binding-revision")]
    [InlineData("attachment-revision")]
    [InlineData("binding-id")]
    [InlineData("identity")]
    [InlineData("detached")]
    public async Task SelectionDriftBeforeRead_RefusesBeforeAdditionalHttp(string change)
    {
        using var fixture = new Fixture();
        using var session = await fixture.CaptureAsync();
        fixture.ChangeSelection(change);

        await Should.ThrowAsync<InvalidDataException>(() => session.FetchSnapshotAsync(fixture.Issue));

        fixture.Tokens.Calls.ShouldBe(1);
        fixture.Network.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SelectionDriftDuringSnapshot_DoesNotReturnPartialSnapshot()
    {
        using var fixture = new Fixture();
        using var session = await fixture.CaptureAsync();
        fixture.Network.AfterSnapshot = () => fixture.ChangeSelection("attachment-revision");

        await Should.ThrowAsync<InvalidDataException>(() => session.FetchSnapshotAsync(fixture.Issue));

        fixture.Network.Requests.Count.ShouldBe(7);
        fixture.Tokens.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"data":null}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","id":"other","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":[{"message":"private-detail"}]}""")]
    public async Task InvalidActorEvidence_DoesNotProduceSession(string body)
    {
        using var fixture = new Fixture();
        fixture.Network.ViewerBody = body;

        var error = await Should.ThrowAsync<JsonException>(() => fixture.CaptureAsync());

        error.ToString().ShouldNotContain("private-detail");
        error.ToString().ShouldNotContain("fixture-credential");
        fixture.Network.Requests.Count.ShouldBe(1);
        fixture.Network.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ViewerHttpFailure_DoesNotProduceSessionOrFollowRedirect(int status)
    {
        using var fixture = new Fixture();
        fixture.Network.Status = (HttpStatusCode)status;
        if (status == 301)
            await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() => fixture.CaptureAsync());
        else
            await Should.ThrowAsync<HttpRequestException>(() => fixture.CaptureAsync());
        fixture.Network.Requests.Count.ShouldBe(1);
        fixture.Network.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task UnboundTarget_RefusesBeforeCredentialLookupOrHttp()
    {
        using var fixture = new Fixture();
        fixture.ChangeSelection("detached");

        await Should.ThrowAsync<InvalidDataException>(() => fixture.CaptureAsync());

        fixture.Tokens.Calls.ShouldBe(0);
        fixture.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApiMismatch_RefusesBeforeCredentialLookupOrHttp()
    {
        using var fixture = new Fixture();
        fixture.Binding = fixture.Binding with
        {
            Identity = new GitHubRepositoryTarget("O_owner", "R_repo")
                .ToIdentity(new("https://different.example/api/graphql")),
        };

        await Should.ThrowAsync<InvalidDataException>(() => fixture.CaptureAsync());

        fixture.Tokens.Calls.ShouldBe(0);
        fixture.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationBeforeCapture_DoesNotResolveTargetOrLookupCredential()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => fixture.CaptureAsync(cancellation.Token));

        await fixture.Attachment.DidNotReceive().ReadWithRevisionAsync(Arg.Any<CancellationToken>());
        fixture.Tokens.Calls.ShouldBe(0);
        fixture.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnavailableCredential_DoesNotCreateSessionOrSend()
    {
        using var fixture = new Fixture();
        fixture.Tokens.Unavailable = true;

        await Should.ThrowAsync<GitHubCredentialUnavailableException>(() => fixture.CaptureAsync());

        fixture.Tokens.Calls.ShouldBe(1);
        fixture.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RepositoryMismatch_DoesNotReturnSnapshot()
    {
        using var fixture = new Fixture();
        using var session = await fixture.CaptureAsync();
        fixture.Network.RepositoryId = "R_other";

        await Should.ThrowAsync<JsonException>(() => session.FetchSnapshotAsync(fixture.Issue));

        fixture.Network.Requests.Count.ShouldBe(2);
        fixture.Tokens.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ForeignItemOrigin_RefusesBeforeAdditionalHttp()
    {
        using var fixture = new Fixture();
        using var session = await fixture.CaptureAsync();

        await Should.ThrowAsync<ArgumentException>(() => session.FetchSnapshotAsync(
            new("github", "https://different.example", "I_issue")));

        fixture.Network.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Disposal_ReleasesSessionTransport()
    {
        using var fixture = new Fixture();
        var session = await fixture.CaptureAsync();

        session.Dispose();

        fixture.Network.Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => session.FetchSnapshotAsync(fixture.Issue));
        fixture.Tokens.Calls.ShouldBe(1);
    }

    private static bool IsSnapshotQuery(string query) => GitHubIssueQueries.TryMatch(query, out _);

    private sealed class Fixture : IDisposable
    {
        public IProviderTargetAttachmentStore Attachment { get; } = Substitute.For<IProviderTargetAttachmentStore>();
        public RotatingTokens Tokens { get; } = new();
        public Transport Network { get; } = new();
        public ServiceProvider Services { get; }
        public ExternalWorkItemIdentity Issue { get; }
        public ProviderTargetBinding Binding { get; set; }
        private ProviderTargetAttachmentEnvelope _envelope;

        public Fixture(string host = "github.com", string endpoint = "https://api.github.com/graphql")
        {
            var id = ProviderTargetBindingId.New();
            Binding = new(id, new GitHubRepositoryTarget("O_owner", "R_repo").ToIdentity(new(endpoint)),
                "owner/repo", null, 7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            _envelope = new(11, "observed-worktree-fingerprint", DateTimeOffset.UnixEpoch,
                ProviderTargetAttachmentState.Attached, id);
            Issue = new("github", new Uri(endpoint).GetLeftPart(UriPartial.Authority), "I_issue");
            Attachment.ReadWithRevisionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromResult(Result.Ok(new VersionedProviderTargetAttachment(_envelope, _envelope.Revision))));
            var bindings = Substitute.For<IProviderTargetBindingStore>();
            bindings.FindByIdAsync(Arg.Any<ProviderTargetBindingId>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Result.Ok<ProviderTargetBinding?>(Binding)));
            var config = new TwigConfiguration
            {
                Provider = "github",
                GitHub = new() { Host = host, Repository = "owner/repo" },
                GitHubAccount = "explicit-account",
            };
            var services = new ServiceCollection();
            services.AddSingleton(config);
            services.AddSingleton(Attachment);
            services.AddSingleton(bindings);
            services.AddSingleton<IGitHubTokenSource>(Tokens);
            services.AddSingleton(new GitHubTransportOverride(() => Network));
            services.AddTwigNetworkServices(config);
            Services = services.BuildServiceProvider();
        }

        public Task<GitHubCapturedReadSession> CaptureAsync(CancellationToken ct = default)
            => Services.GetRequiredService<GitHubCapturedReadSessionFactory>().CaptureAsync(ct);

        public void ChangeSelection(string change)
        {
            switch (change)
            {
                case "fingerprint": _envelope = _envelope with { WorktreeFingerprint = "changed" }; break;
                case "attachment-revision": _envelope = _envelope with { Revision = _envelope.Revision + 1 }; break;
                case "binding-revision": Binding = Binding with { Revision = Binding.Revision + 1 }; break;
                case "binding-id":
                    var id = ProviderTargetBindingId.New();
                    Binding = Binding with { Id = id };
                    _envelope = _envelope with { BindingId = id };
                    break;
                case "identity":
                    Binding = Binding with { Identity = new GitHubRepositoryTarget("O_other", "R_other")
                        .ToIdentity(new("https://api.github.com/graphql")) };
                    break;
                case "detached":
                    _envelope = _envelope with { State = ProviderTargetAttachmentState.Detached, BindingId = null };
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        }

        public void Dispose()
        {
            Services.Dispose();
            Network.Dispose();
        }
    }

    private sealed class RotatingTokens : IGitHubTokenSource
    {
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (Unavailable)
                throw new GitHubCredentialUnavailableException("Fixture credential unavailable.");
            return ValueTask.FromResult($"fixture-credential-{Calls}");
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public List<(Uri? Endpoint, string? Authorization, string Query)> Requests { get; } = [];
        public string ViewerBody { get; set; } = """{"data":{"viewer":{"id":"U_actor","login":"observed-login"}},"errors":[]}""";
        public string RepositoryId { get; set; } = "R_repo";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Action? AfterViewer { get; set; }
        public Action? AfterSnapshot { get; set; }
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = document.RootElement.GetProperty("query").GetString()!;
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString(), query));
            string body;
            if (query == GitHubIssueQueries.AuthenticatedViewer)
            {
                document.RootElement.GetProperty("variables").EnumerateObject().ShouldBeEmpty();
                AfterViewer?.Invoke();
                body = ViewerBody;
            }
            else if (query == GitHubIssueQueries.Snapshot)
            {
                AfterSnapshot?.Invoke();
                body = $$$$$$"""
                    {"data":{"node":{"__typename":"Issue","id":"I_issue","number":1,"title":"Title","state":"OPEN",
                    "body":"","url":"https://example.test/issue","issueType":null,"parent":null,"milestone":null,
                    "repository":{"id":"{{{{{{RepositoryId}}}}}}","nameWithOwner":"owner/repo","owner":{"id":"O_owner"}}}}}
                    """;
            }
            else
            {
                var field = new[] { "labels", "assignees", "subIssues", "blockedBy", "blocking" }
                    .Single(f => query == GitHubIssueQueries.Connection(f));
                body = $$$$$$"""
                    {"data":{"node":{"__typename":"Issue","id":"I_issue","{{{{{{field}}}}}}":{
                    "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                    """;
            }
            return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
