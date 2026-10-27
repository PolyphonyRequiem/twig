using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubSchemaMetadataTests
{
    [Fact]
    public async Task Discovery_ReportsIssueFieldsStatesAndSupportedRelationships_WithoutRuleOrValueCatalogs()
    {
        using var transport = new SchemaTransport(Schema(includeSubIssues: true));
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);

        var metadata = await provider.DiscoverSchemaMetadataAsync();

        metadata.ProviderKind.ShouldBe("github");
        metadata.Availability.ShouldBe(ProviderSchemaDiscoveryAvailability.Available);
        metadata.ReasonCode.ShouldBeNull();
        metadata.Types.ShouldBe([
            new ProviderSchemaTypeMetadata("Issue", ProviderSchemaElementSupport.Supported, null),
        ]);
        metadata.Fields.Single(field => field.Name == "title").ShouldBe(
            new ProviderSchemaFieldMetadata("title", "String!", ProviderSchemaElementSupport.Supported, null));
        foreach (var name in new[] { "labels", "assignees", "milestone" })
            metadata.Fields.Single(field => field.Name == name).Support.ShouldBe(ProviderSchemaElementSupport.Supported);
        metadata.States.Name.ShouldBe("IssueState");
        metadata.States.Support.ShouldBe(ProviderSchemaElementSupport.Supported);
        metadata.States.Values.ShouldBe(["CLOSED", "OPEN"]);
        metadata.Relationships.Single(relation => relation.Kind == LinkKind.ParentChild).Support
            .ShouldBe(ProviderSchemaElementSupport.Supported);
        metadata.Relationships.Single(relation => relation.Kind == LinkKind.PredecessorSuccessor).Support
            .ShouldBe(ProviderSchemaElementSupport.Supported);
        var related = metadata.Relationships.Single(relation => relation.Kind == LinkKind.Related);
        related.Support.ShouldBe(ProviderSchemaElementSupport.Unsupported);
        related.Fields.ShouldBeEmpty();
        related.ReasonCode.ShouldBe("relationship-not-supported-by-adapter");
        metadata.Rules.ShouldBe(new ProviderSchemaRuleSupport(
            ProviderSchemaElementSupport.Unsupported, "provider-does-not-expose-process-rules",
            ProviderSchemaElementSupport.Unsupported, "provider-does-not-expose-field-rules"));

        transport.Requests.ShouldBe(1);
        transport.LastQuery.ShouldBe(GitHubIssueQueries.SchemaMetadata);
        transport.LastEndpoint.ShouldBe("https://api.github.com/graphql");
    }

    [Fact]
    public async Task Discovery_OnOlderGitHubHost_ReportsMissingSchemaElementsAsUnsupported()
    {
        using var transport = new SchemaTransport(
            Schema(includeSubIssues: false, includeIssueType: false, includeDependencies: false));
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http, new Uri("https://ghe.example:8443/api/graphql"));

        var metadata = await provider.DiscoverSchemaMetadataAsync();

        metadata.Availability.ShouldBe(ProviderSchemaDiscoveryAvailability.Available);
        var issueType = metadata.Fields.Single(field => field.Name == "issueType");
        issueType.TypeName.ShouldBeNull();
        issueType.Support.ShouldBe(ProviderSchemaElementSupport.Unsupported);
        issueType.ReasonCode.ShouldBe("field-not-in-schema");
        var parentChild = metadata.Relationships.Single(relation => relation.Kind == LinkKind.ParentChild);
        parentChild.Support.ShouldBe(ProviderSchemaElementSupport.Unsupported);
        parentChild.Fields.ShouldBe(["parent", "subIssues"]);
        parentChild.ReasonCode.ShouldBe("relationship-fields-not-in-schema");
        metadata.Relationships.Single(relation => relation.Kind == LinkKind.PredecessorSuccessor).Support
            .ShouldBe(ProviderSchemaElementSupport.Unsupported);
        transport.LastEndpoint.ShouldBe("https://ghe.example:8443/api/graphql");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "unavailable", "read-denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "unknown", "read-failed")]
    [InlineData(HttpStatusCode.InternalServerError, "unknown", "read-failed")]
    public async Task DiscoveryFailure_DistinguishesUnavailableFromUnknownAndDoesNotInventAbsence(
        HttpStatusCode status,
        string expectedAvailability,
        string expectedReason)
    {
        using var transport = new SchemaTransport("{}", status);
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);

        var metadata = await provider.DiscoverSchemaMetadataAsync();

        metadata.Availability.ToString().ToLowerInvariant().ShouldBe(expectedAvailability);
        metadata.ReasonCode.ShouldBe(expectedReason);
        metadata.Types.ShouldAllBe(type => type.Support == ProviderSchemaElementSupport.Unknown);
        metadata.Fields.ShouldAllBe(field => field.Support == ProviderSchemaElementSupport.Unknown);
        metadata.States.Support.ShouldBe(ProviderSchemaElementSupport.Unknown);
        metadata.Relationships.Where(relation => relation.Kind != LinkKind.Related)
            .ShouldAllBe(relation => relation.Support == ProviderSchemaElementSupport.Unknown);
        metadata.Rules.Process.ShouldBe(ProviderSchemaElementSupport.Unsupported);
        metadata.Rules.Fields.ShouldBe(ProviderSchemaElementSupport.Unsupported);
        transport.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task PartialGraphQlResponse_IsUnknownRatherThanAConfirmedEmptySchema()
    {
        using var transport = new SchemaTransport(
            """{"data":{"issue":{"kind":"OBJECT","name":"Issue","fields":[]},"issueState":null},"errors":[{"message":"introspection unavailable"}]}""");
        using var http = new HttpClient(transport);
        var provider = new GitHubWorkItemProvider(http);

        var metadata = await provider.DiscoverSchemaMetadataAsync();

        metadata.Availability.ShouldBe(ProviderSchemaDiscoveryAvailability.Unknown);
        metadata.ReasonCode.ShouldBe("read-incomplete");
        metadata.Fields.ShouldAllBe(field => field.Support == ProviderSchemaElementSupport.Unknown);
    }

    private static string Schema(
        bool includeSubIssues,
        bool includeIssueType = true,
        bool includeDependencies = true)
    {
        var fields = new List<string>
        {
            Field("number", "Int!"),
            Field("title", "String!"),
            Field("state", "IssueState!"),
            Field("body", "String"),
            Field("url", "URI!"),
            Field("parent", "Issue"),
            Field("milestone", "Milestone"),
            Field("repository", "Repository!"),
            Field("labels", "LabelConnection!"),
            Field("assignees", "UserConnection!"),
        };
        if (includeDependencies)
        {
            fields.Add(Field("blockedBy", "IssueConnection!"));
            fields.Add(Field("blocking", "IssueConnection!"));
        }
        if (includeIssueType)
            fields.Add(Field("issueType", "IssueType"));
        if (includeSubIssues)
            fields.Add(Field("subIssues", "IssueConnection!"));
        return "{\"data\":{\"issue\":{\"kind\":\"OBJECT\",\"name\":\"Issue\",\"fields\":["
            + string.Join(",", fields)
            + "]},\"issueState\":{\"kind\":\"ENUM\",\"name\":\"IssueState\","
            + "\"enumValues\":[{\"name\":\"OPEN\"},{\"name\":\"CLOSED\"}]}}}";
    }

    private static string Field(string name, string typeName)
    {
        var required = typeName.EndsWith('!');
        var namedType = required ? typeName[..^1] : typeName;
        var kind = namedType.EndsWith("Connection", StringComparison.Ordinal) ? "OBJECT"
            : namedType is "Issue" or "Milestone" or "Repository" or "IssueType" ? "OBJECT"
            : namedType is "IssueState" ? "ENUM"
            : namedType is "Int" ? "SCALAR"
            : "SCALAR";
        var type = $$"""{"kind":"{{kind}}","name":"{{namedType}}","ofType":null}""";
        if (required)
            type = $$"""{"kind":"NON_NULL","name":null,"ofType":{{type}}}""";
        return $$"""{"name":"{{name}}","type":{{type}}}""";
    }

    private sealed class SchemaTransport(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastQuery { get; private set; }
        public string? LastEndpoint { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastEndpoint = request.RequestUri?.ToString();
            return ReadBodyAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            LastQuery = document.RootElement.GetProperty("query").GetString();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
