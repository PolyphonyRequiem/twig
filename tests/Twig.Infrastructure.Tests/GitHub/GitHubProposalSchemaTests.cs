using Shouldly;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubProposalSchemaTests
{
    private static ProviderCreateOperation Creation() => new()
    {
        Id = "create", CreationId = Guid.Parse("018f3f5a-6b7c-7d8e-9f01-234567890abc"),
        Concurrency = "observed-non-atomic",
        Destination = GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql")),
        Fields = new Dictionary<string, string?>
        {
            ["System.Title"] = "Example title", ["System.Description"] = "**Native Markdown**",
        },
    };

    private static string Document(ProviderCreateOperation operation) => PlanDocumentWriter.Write(new()
    {
        Version = 2, Workspace = new() { Organization = "github.com", Project = "owner/repo" },
        Operations = [operation],
    });

    [Fact]
    public void Create_RoundTripsItsExactIntentAndDigestsConcurrencyDestinationAndFields()
    {
        var operation = Creation();
        var parser = new PlanDocumentParser();
        var parsed = parser.Parse(Document(operation));
        parsed.IsValid.ShouldBeTrue();
        var roundTrip = parsed.Plan!.Operations.Single().ShouldBeOfType<ProviderCreateOperation>();
        roundTrip.CreationId.ShouldBe(operation.CreationId);
        roundTrip.Destination.ShouldBe(operation.Destination);
        roundTrip.Concurrency.ShouldBe(operation.Concurrency);
        roundTrip.Fields.ShouldBe(operation.Fields);
        parser.Parse(PlanDocumentWriter.Write(parsed.Plan)).Digest.ShouldBe(parsed.Digest);
        parser.Parse(Document(operation with { Concurrency = "exact-revision" })).Digest.ShouldNotBe(parsed.Digest);
        parser.Parse(Document(operation with { CreationId = Guid.NewGuid() })).Digest.ShouldNotBe(parsed.Digest);
        parser.Parse(Document(operation with
        {
            Fields = new Dictionary<string, string?> { ["System.Title"] = "Changed" },
        })).Digest.ShouldNotBe(parsed.Digest);
        GitHubNativeMutationService.Supports(operation).ShouldBeTrue();
        GitHubNativeMutationService.Supports(operation with { Concurrency = "exact-revision" }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"creationId\":", "\"unexpectedCreationId\":")]
    [InlineData("018f3f5a-6b7c-7d8e-9f01-234567890abc", "00000000-0000-0000-0000-000000000000")]
    [InlineData("018f3f5a-6b7c-7d8e-9f01-234567890abc", "not-a-uuid")]
    [InlineData("\"version\":2", "\"version\":1")]
    [InlineData("\"observed-non-atomic\"", "\"blind\"")]
    [InlineData("\"destination\":", "\"unexpectedDestination\":")]
    [InlineData("\"providerKind\":", "\"providerKind\":\"github\",\"providerKind\":")]
    [InlineData("\"descriptorKind\":", "\"unexpectedDescriptorKind\":")]
    public void Create_RejectsMalformedOrUnknownContract(string from, string to)
    {
        var json = Document(Creation());
        json.ShouldContain(from);
        var result = new PlanDocumentParser().Parse(json.Replace(from, to, StringComparison.Ordinal));
        result.IsValid.ShouldBeFalse();
        result.Digest.ShouldBeNull();
    }
}
