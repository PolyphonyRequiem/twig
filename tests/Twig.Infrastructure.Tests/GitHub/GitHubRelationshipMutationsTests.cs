using System.Text.Json;
using Shouldly;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubRelationshipMutationsTests
{
    private const string Scope = "https://api.github.com";

    [Theory]
    [InlineData(PlanOperationKind.AddLink, "parent", "addSubIssue", "other", "subIssueId", "source")]
    [InlineData(PlanOperationKind.RemoveLink, "parent", "removeSubIssue", "other", "subIssueId", "source")]
    [InlineData(PlanOperationKind.AddLink, "predecessor", "addBlockedBy", "source", "blockingIssueId", "other")]
    [InlineData(PlanOperationKind.RemoveLink, "predecessor", "removeBlockedBy", "source", "blockingIssueId", "other")]
    [InlineData(PlanOperationKind.AddLink, "successor", "addBlockedBy", "other", "blockingIssueId", "source")]
    [InlineData(PlanOperationKind.RemoveLink, "successor", "removeBlockedBy", "other", "blockingIssueId", "source")]
    public void Create_MapsExactDocumentAndDirection(
        PlanOperationKind kind, string relation, string mutation, string issue, string peerKey, string peer)
    {
        var operation = Operation(kind, relation);
        var request = GitHubRelationshipMutations.Create(operation, Scope);
        var replace = mutation == "addSubIssue" ? ", replaceParent: false" : "";
        request.Query.ShouldBe(
            $"mutation($issueId: ID!, ${peerKey}: ID!) {{ "
            + $"{mutation}(input: {{issueId: $issueId, {peerKey}: ${peerKey}{replace}}}) {{ clientMutationId }} "
            + "}");
        request.Variables.Count.ShouldBe(2);
        request.Variables["issueId"].ShouldBe(issue);
        request.Variables[peerKey].ShouldBe(peer);
        operation.Target.ExpectedProviderRevision.Value.ShouldBe("opaque-revision");
        GitHubIssueQueries.TryMatch(request.Query, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("predecessor")]
    [InlineData("successor")]
    public void Serialize_OpaqueIdsStayInVariables(string relation)
    {
        const string id = "opaque\"}\nmutation { deleteIssue }\t\\value";
        var operation = Operation(PlanOperationKind.AddLink, relation);
        operation = operation with
        {
            Target = operation.Target with
            {
                Item = operation.Target.Item with { ExternalIdentity = new("github", Scope, id) },
            },
        };
        var request = GitHubRelationshipMutations.Create(operation, Scope);
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.GitHubIssueGraphQlRequest);
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("query").GetString().ShouldBe(request.Query);
        request.Query.ShouldNotContain(id);
        var key = relation == "parent" ? "subIssueId" : relation == "predecessor" ? "issueId" : "blockingIssueId";
        parsed.RootElement.GetProperty("variables").GetProperty(key).GetString().ShouldBe(id);
        parsed.RootElement.EnumerateObject().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData("source-provider")]
    [InlineData("peer-provider")]
    [InlineData("source-origin")]
    [InlineData("peer-origin")]
    [InlineData("missing-peer")]
    [InlineData("fields")]
    [InlineData("empty-fields")]
    [InlineData("same-native")]
    [InlineData("same-local")]
    public void Create_RefusesInvalidEndpointsOrShape(string invalid)
    {
        var operation = Operation(PlanOperationKind.AddLink, "parent");
        operation = invalid switch
        {
            "source-provider" => operation with
            {
                Target = operation.Target with
                {
                    Item = operation.Target.Item with { ExternalIdentity = new("ado", Scope, "source") },
                },
            },
            "peer-provider" => operation with { Other = operation.Other! with { ExternalIdentity = new("ado", Scope, "other") } },
            "source-origin" => operation with
            {
                Target = operation.Target with
                {
                    Item = operation.Target.Item with { ExternalIdentity = new("github", "https://other.example", "source") },
                },
            },
            "peer-origin" => operation with { Other = operation.Other! with { ExternalIdentity = new("github", "https://other.example", "other") } },
            "missing-peer" => operation with { Other = null },
            "fields" => operation with { Fields = new Dictionary<string, string?> { ["title"] = "value" } },
            "empty-fields" => operation with { Fields = new Dictionary<string, string?>() },
            "same-native" => operation with { Other = operation.Other! with { ExternalIdentity = operation.Target.Item.ExternalIdentity } },
            "same-local" => operation with { Other = operation.Other! with { LocalId = operation.Target.Item.LocalId } },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        Should.Throw<ArgumentException>(() => GitHubRelationshipMutations.Create(operation, Scope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("related")]
    [InlineData("child")]
    [InlineData("Parent")]
    [InlineData("parent ")]
    [InlineData("mutation { deleteIssue }")]
    public void Create_RefusesNoncanonicalOrUnsupportedRelation(string? relation)
    {
        Should.Throw<ArgumentException>(() =>
            GitHubRelationshipMutations.Create(Operation(PlanOperationKind.AddLink, relation), Scope));
    }

    [Theory]
    [InlineData(PlanOperationKind.Batch)]
    [InlineData(PlanOperationKind.PublishSeed)]
    [InlineData(PlanOperationKind.Delete)]
    [InlineData(PlanOperationKind.AppendComment)]
    public void Create_RefusesNonLinkOperation(PlanOperationKind kind)
    {
        Should.Throw<ArgumentException>(() =>
            GitHubRelationshipMutations.Create(Operation(kind, "parent"), Scope));
    }

    [Fact]
    public void Create_RequiresExactSelectedApiOrigin()
    {
        Should.Throw<ArgumentException>(() => GitHubRelationshipMutations.Create(
            Operation(PlanOperationKind.AddLink, "parent"), "https://API.GITHUB.com"));
    }

    private static ProviderMutationOperation Operation(PlanOperationKind kind, string? relation) => new()
    {
        Id = "relationship",
        OperationKind = kind,
        Target = new()
        {
            Item = new()
            {
                LocalId = LocalWorkItemId.FromGuid(Guid.Parse("019a0000-0000-7000-8000-000000000001")),
                ExternalIdentity = new("github", Scope, "source"),
            },
            ExpectedProviderRevision = new("opaque-revision"),
        },
        Other = new()
        {
            LocalId = LocalWorkItemId.FromGuid(Guid.Parse("019a0000-0000-7000-8000-000000000002")),
            ExternalIdentity = new("github", Scope, "other"),
        },
        Relation = relation,
    };
}
