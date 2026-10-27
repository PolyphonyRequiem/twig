using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubWorkItemProviderTests
{
    private static readonly ExternalWorkItemIdentity Identity = new("github", "https://api.github.com", "I_caseSensitive");

    [Fact]
    public async Task Snapshot_UsesQualifiedNodeIdentityAndTypedFields_NotAliasesOrSyntheticTokens()
    {
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);

        var snapshot = await provider.FetchSnapshotAsync(Identity);

        snapshot.Identity.ShouldBe(Identity);
        snapshot.RevisionToken.ShouldNotBeNull().Value.ShouldStartWith("github-observed:");
        snapshot.Fields["System.Title"].Value.ShouldBe(new TextValue("Issue title"));
        snapshot.Fields["System.State"].Value.ShouldBe(new TextValue("OPEN"));
        snapshot.Fields["System.WorkItemType"].Value.ShouldBe(new TextValue("Bug"));
        snapshot.Fields["System.Description"].Value.ShouldBe(new RichTextValue("**body**", RichTextFormat.Markdown));
        snapshot.Fields["System.AssignedTo"].Value.ShouldBe(new IdentityValue("U_one", "someone"));
        snapshot.Fields["github.number"].Value.ShouldBe(new IntegerValue(42));
        snapshot.Fields["github.labels"].Value.ShouldBe(new StringListValue(["bug"]));
        snapshot.Fields["System.Tags"].Value.ShouldBe(new StringListValue(["bug"]));
        snapshot.RelationshipsComplete.ShouldBeTrue();
        snapshot.Fields["github.milestone"].Value.ShouldBe(NullValue.Instance);
        (await provider.ResolveResourceScopeAsync()).ShouldBe(Identity.ResourceScope);
        transport.Requests.ShouldNotBeEmpty();
        transport.Requests.ShouldAllBe(r => r.Method == HttpMethod.Post && r.Uri == "https://api.github.com/graphql");
    }

    [Fact]
    public async Task Relationships_AreNativeDirectedQualifiedAndPaginated()
    {
        using var transport = new RecordingTransport { PaginateChildren = true };
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);

        var snapshot = await provider.FetchSnapshotAsync(Identity);

        snapshot.Relationships.ShouldBe([
            Edge("I_parent", "I_caseSensitive", LinkKind.ParentChild),
            Edge("I_caseSensitive", "I_child", LinkKind.ParentChild),
            Edge("I_caseSensitive", "I_child2", LinkKind.ParentChild),
            Edge("I_blocker", "I_caseSensitive", LinkKind.PredecessorSuccessor),
            Edge("I_caseSensitive", "I_blocked", LinkKind.PredecessorSuccessor),
        ], ignoreOrder: true);
        snapshot.Relationships.ShouldAllBe(r =>
            RelationshipProvenance.OfProviderObservation(provider.Capabilities, r) == RelationshipOrigin.ProviderNative);
        snapshot.Relationships.ShouldAllBe(r =>
            RelationshipProvenance.ToToken(RelationshipProvenance.OfProviderObservation(provider.Capabilities, r))
                == "provider-native");
        provider.Capabilities.For(LinkKind.Related).Implementation.ShouldBe(CapabilityImplementation.Unsupported);
        transport.Requests.ShouldContain(r => r.Body.Contains("\"after\":\"cursor-1\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailure_IsNotAnEmptySnapshotOrRelationshipSet(int status)
    {
        using var transport = new RecordingTransport { Status = (HttpStatusCode)status };
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        (await provider.ProbeCapabilitiesAsync()).ShouldBeEmpty();
        await Should.ThrowAsync<HttpRequestException>(() => provider.FetchSnapshotAsync(Identity));
        var evidence = await provider.ProbeCapabilitiesAsync();
        evidence.Count.ShouldBe(2);
        var expected = status is 401 or 403 or 404 or 410
            ? CapabilityAvailability.Unavailable : CapabilityAvailability.Unknown;
        evidence.ShouldAllBe(e => e.Availability == expected);
        evidence.ShouldAllBe(e => e.ReasonCode == (expected == CapabilityAvailability.Unavailable
            ? "read-denied" : "read-failed"));
        evidence.Select(e => e.Kind).ShouldBe(
            [LinkKind.ParentChild, LinkKind.PredecessorSuccessor], ignoreOrder: true);
        var requestsAfterRead = transport.Requests.Count;
        var report = CapabilityReport.Create(provider.Capabilities, await provider.ProbeCapabilitiesAsync());
        transport.Requests.Count.ShouldBe(requestsAfterRead);
        report.Relationships.Where(e => e.Kind != LinkKind.Related)
            .ShouldAllBe(e => e.Implementation == CapabilityImplementation.Native && e.Availability == expected);
        var related = report.Relationships.Single(e => e.Kind == LinkKind.Related);
        related.Implementation.ShouldBe(CapabilityImplementation.Unsupported);
        related.Availability.ShouldBe(CapabilityAvailability.Unknown);
        related.ReasonCode.ShouldBe(CapabilityReport.NotProbedReason);
        provider.Capabilities.For(LinkKind.ParentChild).Implementation.ShouldBe(CapabilityImplementation.Native);
    }

    [Fact]
    public async Task ReadEvidence_IsScopedToAdapter_NotSharedByProviderKindOrOrigin()
    {
        using var deniedTransport = new RecordingTransport { Status = HttpStatusCode.Forbidden };
        using var otherTransport = new RecordingTransport();
        using var deniedHttp = new HttpClient(deniedTransport);
        using var otherHttp = new HttpClient(otherTransport);
        var denied = new GitHubWorkItemProvider(deniedHttp);
        var other = new GitHubWorkItemProvider(otherHttp);

        await Should.ThrowAsync<HttpRequestException>(() => denied.FetchSnapshotAsync(Identity));
        var deniedEvidence = await denied.ProbeCapabilitiesAsync();
        deniedEvidence.Count.ShouldBe(2);
        deniedEvidence.ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unavailable && e.ReasonCode == "read-denied");

        var beforeRead = CapabilityReport.Create(other.Capabilities, await other.ProbeCapabilitiesAsync());
        beforeRead.Relationships.ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unknown && e.ReasonCode == CapabilityReport.NotProbedReason);
        otherTransport.Requests.ShouldBeEmpty();

        var snapshot = await other.FetchSnapshotAsync(Identity);
        snapshot.RelationshipsComplete.ShouldBeTrue();
        snapshot.Relationships.ShouldNotBeEmpty();
        var requestsAfterRead = otherTransport.Requests.Count;
        var afterRead = CapabilityReport.Create(other.Capabilities, await other.ProbeCapabilitiesAsync());
        afterRead.Relationships.ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unknown && e.ReasonCode == CapabilityReport.NotProbedReason);
        otherTransport.Requests.Count.ShouldBe(requestsAfterRead);
        (await denied.ProbeCapabilitiesAsync()).ShouldBe(deniedEvidence);
    }

    [Theory]
    [InlineData("""{"data":{"node":null}}""")]
    [InlineData("""{"data":{"node":{"id":"I_caseSensitive"}},"errors":[{"message":"Forbidden"}]}""")]
    [InlineData("""{"data":{"node":{"__typename":"PullRequest","id":"I_caseSensitive"}}}""")]
    [InlineData("""{"data":{"node":{"__typename":"Issue","id":"I_different"}}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""not-json""")]
    public async Task MissingPartialWrongIdentityOrNonIssueData_FailsExplicitly(string json)
    {
        using var transport = new RecordingTransport { OverrideJson = json };
        using var http = new HttpClient(transport);
        await Should.ThrowAsync<JsonException>(() => new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity));
    }

    [Theory]
    [InlineData("ado", "https://api.github.com")]
    [InlineData("github", "https://other.example")]
    [InlineData("github", "https://API.GITHUB.COM/")]
    public async Task ForeignOrNoncanonicalIdentity_IsRefusedWithoutHttp(string kind, string scope)
    {
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        await Should.ThrowAsync<ArgumentException>(() =>
            new GitHubWorkItemProvider(http).FetchSnapshotAsync(new(kind, scope, "I_caseSensitive")));
        transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://API.GITHUB.COM:443/graphql", "https://api.github.com")]
    [InlineData("https://GHE.example:8443/api/graphql", "https://ghe.example:8443")]
    public async Task Scope_IsNormalizedApiOrigin_NotRepositoryOrActor(string endpoint, string scope)
    {
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        (await new GitHubWorkItemProvider(http, new Uri(endpoint)).ResolveResourceScopeAsync()).ShouldBe(scope);
        transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://api.github.com/graphql")]
    [InlineData("https://someone:secret@api.github.com/graphql")]
    [InlineData("https://api.github.com/graphql?token=anything")]
    [InlineData("https://api.github.com/graphql#fragment")]
    [InlineData("https://api.github.com/repos/owner/repo")]
    public void InvalidEndpoint_IsRefused(string endpoint)
    {
        using var http = new HttpClient(new RecordingTransport());
        Should.Throw<ArgumentException>(() => new GitHubWorkItemProvider(http, new Uri(endpoint)));
    }

    [Fact]
    public async Task MultipleAssignees_ArePreservedDeterministically_NotMisrepresentedAsOne()
    {
        using var transport = new RecordingTransport();
        transport.Overrides["assignees"] = Connection("assignees",
            """{"id":"U_z","login":"zeta"},{"id":"U_a","login":"alpha"}""");
        using var http = new HttpClient(transport);
        var snapshot = await new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity);
        snapshot.Fields.ContainsKey("System.AssignedTo").ShouldBeFalse();
        snapshot.Fields["github.assignees"].Value.ShouldBe(new RawValue(
            """[{"id":"U_a","login":"alpha"},{"id":"U_z","login":"zeta"}]""", "github.assignees.v1"));
    }

    [Theory]
    [InlineData("new/repo", true)]
    [InlineData("NEW/Repo", true)]
    [InlineData("other/repo", false)]
    public async Task ExpectedRepository_RefusesIssuesOutsideTheConfiguredRepository(string configured, bool admitted)
    {
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http, expectedRepository: configured);

        if (admitted)
            (await provider.FetchSnapshotAsync(Identity)).Identity.ShouldBe(Identity);
        else
            await Should.ThrowAsync<JsonException>(() => provider.FetchSnapshotAsync(Identity));
    }

    [Fact]
    public async Task ObservedEmpty_IsExplicitNullOrEmpty_NotAnUnavailableRead()
    {
        using var transport = new RecordingTransport
        {
            Metadata = RecordingTransport.DefaultMetadata.Replace(
                "\"parent\":{\"id\":\"I_parent\"}", "\"parent\":null", StringComparison.Ordinal)
                .Replace("\"body\":\"**body**\"", "\"body\":\"\"", StringComparison.Ordinal),
        };
        foreach (var field in new[] { "assignees", "labels", "subIssues", "blockedBy", "blocking" })
            transport.Overrides[field] = Connection(field, "");
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        var snapshot = await provider.FetchSnapshotAsync(Identity);
        snapshot.Relationships.ShouldBeEmpty();
        snapshot.RelationshipsComplete.ShouldBeTrue();
        snapshot.Fields["System.AssignedTo"].Value.ShouldBe(NullValue.Instance);
        snapshot.Fields["System.Description"].Value.ShouldBe(NullValue.Instance);
        snapshot.Fields["github.labels"].Value.ShouldBe(new StringListValue([]));
        (await provider.ProbeCapabilitiesAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"nodes":null,"pageInfo":{"hasNextPage":false,"endCursor":null}}""")]
    [InlineData("""{"nodes":[null],"pageInfo":{"hasNextPage":false,"endCursor":null}}""")]
    [InlineData("""{"nodes":[{}],"pageInfo":{"hasNextPage":false,"endCursor":null}}""")]
    [InlineData("""{"nodes":[{"id":" "}],"pageInfo":{"hasNextPage":false,"endCursor":null}}""")]
    [InlineData("""{"nodes":[],"pageInfo":null}""")]
    [InlineData("""{"nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":null}}""")]
    [InlineData("""{"nodes":[],"pageInfo":{"hasNextPage":"false","endCursor":null}}""")]
    public async Task IncompleteRelationshipConnection_IsNotAnEmptyObservation(string connection)
    {
        using var transport = new RecordingTransport();
        transport.Overrides["subIssues"] = "{\"data\":{\"node\":{\"__typename\":\"Issue\",\"id\":\"I_caseSensitive\",\"subIssues\":"
            + connection + "}}}";
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        await Should.ThrowAsync<JsonException>(() => provider.FetchSnapshotAsync(Identity));
        (await provider.ProbeCapabilitiesAsync()).ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unknown && e.ReasonCode == "read-incomplete");
    }

    [Fact]
    public async Task DeniedRelationshipRead_DoesNotReturnTheSuccessfulPartialSnapshot()
    {
        using var transport = new RecordingTransport();
        transport.Overrides["blockedBy"] =
            """{"data":{"node":null},"errors":[{"type":"FORBIDDEN","message":"Sensitive provider text"}]}""";
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        var exception = await Should.ThrowAsync<JsonException>(() => provider.FetchSnapshotAsync(Identity));
        exception.Message.ShouldNotContain("Sensitive provider text");
        (await provider.ProbeCapabilitiesAsync()).ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unavailable && e.ReasonCode == "read-denied");
        provider.Capabilities.For(LinkKind.PredecessorSuccessor).Implementation.ShouldBe(CapabilityImplementation.Native);
    }

    [Fact]
    public async Task RepeatedCursor_FailsRatherThanHangingOrReturningTruncatedRelationships()
    {
        using var transport = new RecordingTransport();
        transport.Overrides["subIssues"] = Connection("subIssues", """{"id":"I_child"}""", more: true);
        using var http = new HttpClient(transport);
        var exception = await Should.ThrowAsync<JsonException>(() =>
            new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity));
        exception.Message.ShouldContain("pagination did not advance");
        transport.Requests.Count.ShouldBe(5);
    }

    [Fact]
    public async Task DuplicateNativeEdges_CollapseToOneCanonicalEdge()
    {
        using var transport = new RecordingTransport();
        transport.Overrides["subIssues"] = Connection("subIssues", """{"id":"I_child"},{"id":"I_child"}""");
        using var http = new HttpClient(transport);
        var snapshot = await new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity);
        snapshot.Relationships.Count(e => e.Kind == LinkKind.ParentChild && e.Target.NativeId == "I_child").ShouldBe(1);
    }

    [Fact]
    public async Task CancelledRead_DoesNotSendRequestsOrReturnASnapshot()
    {
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity, cancellation.Token));
        transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("subIssues", """{"id":"I_first"}""", """{"id":"I_second"}""")]
    [InlineData("blockedBy", """{"id":"I_first"}""", """{"id":"I_second"}""")]
    [InlineData("blocking", """{"id":"I_first"}""", """{"id":"I_second"}""")]
    [InlineData("labels", """{"name":"first"}""", """{"name":"second"}""")]
    [InlineData("assignees", """{"id":"U_z","login":"zeta"}""", """{"id":"U_a","login":"alpha"}""")]
    public async Task EveryConnection_FollowsItsOwnCursor(string field, string first, string second)
    {
        using var transport = new RecordingTransport
        {
            ResponseOverride = (query, variables) => query.Contains(field + "(", StringComparison.Ordinal)
                ? variables.GetProperty("after").ValueKind == JsonValueKind.Null
                    ? Connection(field, first, more: true) : Connection(field, second)
                : null,
        };
        using var http = new HttpClient(transport);
        var snapshot = await new GitHubWorkItemProvider(http).FetchSnapshotAsync(Identity);
        transport.Requests.Count(r => r.Body.Contains(field + "(", StringComparison.Ordinal)).ShouldBe(2);
        if (field == "labels")
            snapshot.Fields["github.labels"].Value.ShouldBe(new StringListValue(["first", "second"]));
        else if (field == "assignees")
        {
            snapshot.Fields.ContainsKey("System.AssignedTo").ShouldBeFalse();
            snapshot.Fields["github.assignees"].Value.ShouldBe(new RawValue(
                """[{"id":"U_a","login":"alpha"},{"id":"U_z","login":"zeta"}]""", "github.assignees.v1"));
        }
        else
        {
            snapshot.Relationships.ShouldContain(e => e.Source.NativeId == "I_first" || e.Target.NativeId == "I_first");
            snapshot.Relationships.ShouldContain(e => e.Source.NativeId == "I_second" || e.Target.NativeId == "I_second");
        }
    }

    [Fact]
    public async Task LaterPageFailure_IsNotACompletedRelationshipSet()
    {
        using var transport = new RecordingTransport
        {
            ResponseOverride = (query, variables) => query.Contains("subIssues(", StringComparison.Ordinal)
                ? variables.GetProperty("after").ValueKind == JsonValueKind.Null
                    ? Connection("subIssues", """{"id":"I_first"}""", more: true)
                    : """{"data":{"node":null},"errors":[{"type":"FORBIDDEN"}]}"""
                : null,
        };
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        await Should.ThrowAsync<JsonException>(() => provider.FetchSnapshotAsync(Identity));
        (await provider.ProbeCapabilitiesAsync()).ShouldAllBe(e => e.ReasonCode == "read-denied");
    }

    [Fact]
    public async Task SubsequentSuccessfulRead_ClearsFailureEvidence_ButDoesNotProveWritePermission()
    {
        var deny = true;
        using var transport = new RecordingTransport
        {
            ResponseOverride = (_, _) => deny ? """{"errors":[{"type":"FORBIDDEN"}]}""" : null,
        };
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);
        await Should.ThrowAsync<JsonException>(() => provider.FetchSnapshotAsync(Identity));
        (await provider.ProbeCapabilitiesAsync()).ShouldNotBeEmpty();
        deny = false;
        await provider.FetchSnapshotAsync(Identity);
        var requestCount = transport.Requests.Count;
        var report = CapabilityReport.Create(provider.Capabilities, await provider.ProbeCapabilitiesAsync());
        report.Relationships.ShouldAllBe(r =>
            r.Availability == CapabilityAvailability.Unknown && r.ReasonCode == CapabilityReport.NotProbedReason);
        transport.Requests.Count.ShouldBe(requestCount);
    }

    private static ProviderWorkItemRelationship Edge(string source, string target, LinkKind kind)
        => new(new("github", Identity.ResourceScope, source), new("github", Identity.ResourceScope, target), kind);

    private static string Connection(string field, string nodes, bool more = false)
        => $$$"""
            {"data":{"node":{"__typename":"Issue","id":"I_caseSensitive","{{{field}}}":{
            "nodes":[{{{nodes}}}],"pageInfo":{"hasNextPage":{{{(more ? "true" : "false")}}},
            "endCursor":{{{(more ? "\"cursor-1\"" : "null")}}}
            }
            }
            }
            }
            }
            """;

    internal sealed class RecordingTransport : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? OverrideJson { get; init; }
        public bool PaginateChildren { get; init; }
        public Dictionary<string, string> Overrides { get; } = new(StringComparer.Ordinal);
        public Func<string, JsonElement, string?>? ResponseOverride { get; init; }
        public Func<string, Task>? BeforeResponseAsync { get; init; }
        public string Metadata { get; init; } = DefaultMetadata;
        public const string DefaultMetadata = """
            {"data":{"node":{"__typename":"Issue","id":"I_caseSensitive","number":42,
            "title":"Issue title","state":"OPEN","body":"**body**","url":"https://github.com/new/repo/issues/42",
            "issueType":{"name":"Bug"},"parent":{"id":"I_parent"},"milestone":null,
            "repository":{"id":"R_repo","nameWithOwner":"new/repo","owner":{"id":"O_owner"}}}}}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
            request.Headers.UserAgent.ToString().ShouldContain("twig");
            request.Headers.Accept.ToString().ShouldContain("application/vnd.github+json");
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;
            query.ShouldStartWith("query(");
            var variables = document.RootElement.GetProperty("variables");
            variables.GetProperty("id").GetString().ShouldBe(Identity.NativeId);
            if (BeforeResponseAsync is not null)
                await BeforeResponseAsync(query);
            var json = OverrideJson ?? ResponseOverride?.Invoke(query, variables) ?? Response(query, variables);
            return new(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private string Response(string query, JsonElement variables)
        {
            foreach (var (field, json) in Overrides)
                if (query.Contains(field + "(", StringComparison.Ordinal))
                    return json;
            if (query.Contains("subIssues(", StringComparison.Ordinal))
            {
                var next = variables.GetProperty("after").ValueKind != JsonValueKind.Null;
                return Connection("subIssues", next ? """{"id":"I_child2"}""" : """{"id":"I_child"}""",
                    PaginateChildren && !next);
            }
            if (query.Contains("blockedBy(", StringComparison.Ordinal))
                return Connection("blockedBy", """{"id":"I_blocker"}""");
            if (query.Contains("blocking(", StringComparison.Ordinal))
                return Connection("blocking", """{"id":"I_blocked"}""");
            if (query.Contains("labels(", StringComparison.Ordinal))
                return Connection("labels", """{"name":"bug"}""");
            if (query.Contains("assignees(", StringComparison.Ordinal))
                return Connection("assignees", """{"id":"U_one","login":"someone"}""");
            return Metadata;
        }
    }
}
